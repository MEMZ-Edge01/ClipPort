using ClipPort.FnOS.Contracts;
using ClipPort.Models;
using ClipPort.Services;

namespace ClipPort.FnOS.FnOs;

public sealed record ValidatedTaskRequest(
    CreateTaskRequest Request,
    string SourcePath,
    string DestinationPath);

public sealed class AccessValidationException(
    string code,
    string message,
    object? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public object? Details { get; } = details;
}

/// <summary>
/// Deep module for fnOS file authorization. Callers provide an identity and a
/// requested operation; the module owns system queries, path normalization,
/// authorization-root containment, ACL checks and overlap validation.
/// </summary>
public sealed class AuthorizedFolderModule(
    IFnOsOpenApi openApi,
    ILogger<AuthorizedFolderModule> logger)
{
    public async Task RevokeAsync(string path, CancellationToken cancellationToken)
    {
        string normalized = NormalizePath(path);
        IReadOnlyList<string> authorized =
            await openApi.GetSharedAccessibleFoldersAsync(cancellationToken);
        bool isAuthorizedRoot = authorized.Any(value =>
            TryNormalizePath(value, out string candidate) &&
            PathSemantics.Comparer.Equals(candidate, normalized));
        if (!isAuthorizedRoot)
        {
            throw new AccessValidationException(
                "path_not_authorized",
                "Only a currently authorized root folder can be revoked.");
        }
        await openApi.DeleteSharedAccessibleFolderAsync(normalized, cancellationToken);
    }

    public async Task<string> ValidateDirectoryAsync(
        int userId,
        string path,
        bool requireWrite,
        CancellationToken cancellationToken)
    {
        string normalized = NormalizePath(path);
        if (!Directory.Exists(normalized))
        {
            throw new AccessValidationException(
                requireWrite ? "path_not_writable" : "path_not_readable",
                "The selected directory does not exist.");
        }
        IReadOnlyList<string> roots = await openApi.GetSharedAccessibleFoldersAsync(cancellationToken);
        string[] normalizedRoots = roots
            .Where(value => TryNormalizePath(value, out _))
            .Select(Path.GetFullPath)
            .Distinct(PathSemantics.Comparer)
            .ToArray();
        EnsureAuthorized(normalized, normalizedRoots);
        FnOsAclResult? acl = FindAcl(
            await openApi.CheckUserAclAsync(userId, [normalized], cancellationToken),
            normalized);
        if (acl?.Readable != true)
        {
            throw new AccessValidationException(
                "path_not_readable",
                "The current administrator cannot read the selected directory.");
        }
        if (requireWrite && acl.Writable != true)
        {
            throw new AccessValidationException(
                "path_not_writable",
                "The current administrator cannot write to the selected directory.");
        }
        return normalized;
    }

    public async Task<IReadOnlyList<AuthorizedFolderDto>> GetFoldersAsync(
        int userId,
        string language,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> rawPaths =
            await openApi.GetSharedAccessibleFoldersAsync(cancellationToken);
        string[] paths = rawPaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
            .Select(Path.GetFullPath)
            .Distinct(PathSemantics.Comparer)
            .ToArray();
        if (paths.Length == 0)
        {
            return [];
        }

        // The authorized roots are the source of truth. ACL and semantic paths
        // enrich the list for presentation, so a temporary fnOS failure in one
        // of them must not hide every root that the administrator just selected.
        Task<IReadOnlyList<FnOsAclResult>> aclTask = GetOptionalAsync(
            () => openApi.CheckUserAclAsync(userId, paths, cancellationToken),
            Array.Empty<FnOsAclResult>(),
            "permissions");
        Task<IReadOnlyDictionary<string, string>> semanticTask = GetOptionalAsync(
            () => openApi.ConvertPathsAsync(
                paths,
                NormalizeLanguage(language),
                cancellationToken),
            new Dictionary<string, string>(PathSemantics.Comparer),
            "semantic-paths");
        await Task.WhenAll(aclTask, semanticTask);

        var aclByPath = aclTask.Result
            .Select(item => TryNormalizeAcl(item, out FnOsAclResult? normalized)
                ? normalized
                : null)
            .OfType<FnOsAclResult>()
            .GroupBy(item => item.Path, PathSemantics.Comparer)
            .ToDictionary(
                group => group.Key,
                group => new FnOsAclResult(
                    group.Key,
                    group.All(item => item.Readable),
                    group.All(item => item.Writable),
                    group.All(item => item.Deletable)),
                PathSemantics.Comparer);
        var semanticPaths = semanticTask.Result
            .Where(item => !string.IsNullOrWhiteSpace(item.Value) &&
                           TryNormalizePath(item.Key, out _))
            .GroupBy(item => Path.GetFullPath(item.Key), PathSemantics.Comparer)
            .ToDictionary(
                group => group.Key,
                group => group.First().Value,
                PathSemantics.Comparer);
        return paths.Select(path =>
        {
            aclByPath.TryGetValue(path, out FnOsAclResult? acl);
            semanticPaths.TryGetValue(path, out string? semanticPath);
            return new AuthorizedFolderDto(
                path,
                string.IsNullOrWhiteSpace(semanticPath) ? path : semanticPath,
                acl?.Readable == true,
                acl?.Writable == true,
                acl is null ? "unavailable" : "confirmed",
                string.IsNullOrWhiteSpace(semanticPath) ? "fallback" : "confirmed");
        }).ToArray();
    }

    private async Task<T> GetOptionalAsync<T>(
        Func<Task<T>> operation,
        T fallback,
        string enrichment)
    {
        try
        {
            return await operation();
        }
        catch (FnOsOpenApiException ex)
        {
            logger.LogWarning(
                "Optional authorized-folder enrichment {Enrichment} failed: operation={Operation}, code={Code}, status={Status}, reqId={RequestId}",
                enrichment,
                ex.Operation,
                ex.Code,
                ex.HttpStatusCode,
                ex.RequestId);
            return fallback;
        }
    }

    public async Task<ValidatedTaskRequest> ValidateTaskAsync(
        int userId,
        CreateTaskRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath) ||
            string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            throw new AccessValidationException(
                "invalid_request",
                "Source and destination paths are required.");
        }
        if (!Enum.IsDefined(request.Mode) ||
            !Enum.IsDefined(request.ExistingFilePolicy) ||
            !Enum.IsDefined(request.VerificationAlgorithm) ||
            !Enum.IsDefined(request.VerificationExecutionMode))
        {
            throw new AccessValidationException(
                "invalid_request",
                "One or more task options are invalid.");
        }

        string sourcePath = NormalizePath(request.SourcePath);
        string destinationBase = NormalizePath(request.DestinationPath);
        if (!PathSafety.TryResolveSubfolder(
                destinationBase,
                request.DestinationSubfolder,
                out string destinationPath))
        {
            throw new AccessValidationException(
                "invalid_request",
                "The destination subfolder name is invalid.");
        }

        if (!Directory.Exists(sourcePath))
        {
            throw new AccessValidationException(
                "path_not_readable",
                "The source directory does not exist.",
                new { path = sourcePath });
        }
        if (!Directory.Exists(destinationBase))
        {
            throw new AccessValidationException(
                "path_not_writable",
                "The destination directory does not exist.",
                new { path = destinationBase });
        }

        IReadOnlyList<string> authorizedRoots =
            await openApi.GetSharedAccessibleFoldersAsync(cancellationToken);
        string[] normalizedRoots = authorizedRoots
            .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
            .Select(NormalizePath)
            .Distinct(PathSemantics.Comparer)
            .ToArray();
        EnsureAuthorized(sourcePath, normalizedRoots);
        EnsureAuthorized(destinationBase, normalizedRoots);

        try
        {
            PathSafety.EnsureDestinationDoesNotTraverseReparsePoint(sourcePath);
            PathSafety.EnsureDestinationDoesNotTraverseReparsePoint(destinationBase);
        }
        catch (IOException ex)
        {
            throw new AccessValidationException(
                "path_overlap",
                "Source or destination traverses a symbolic link.",
                new { reason = ex.Message });
        }
        if (!PathSafety.TryValidateSourceAndDestination(
                sourcePath,
                destinationPath,
                out PathValidationError pathError))
        {
            throw new AccessValidationException(
                "path_overlap",
                "Source and destination paths overlap or are unsafe.",
                new { reason = pathError.ToString() });
        }

        IReadOnlyList<FnOsAclResult> acl = await openApi.CheckUserAclAsync(
            userId,
            [sourcePath, destinationBase],
            cancellationToken);
        FnOsAclResult? sourceAcl = FindAcl(acl, sourcePath);
        FnOsAclResult? destinationAcl = FindAcl(acl, destinationBase);
        if (sourceAcl?.Readable != true)
        {
            throw new AccessValidationException(
                "path_not_readable",
                "The current administrator cannot read the source directory.",
                new { path = sourcePath });
        }

        bool destinationMustBeWritable = request.Mode is not FnOsTaskMode.VerifyOnly;
        if (request.Mode == FnOsTaskMode.VerifyOnly && destinationAcl?.Readable != true)
        {
            throw new AccessValidationException(
                "path_not_readable",
                "The current administrator cannot read the destination directory.",
                new { path = destinationBase });
        }
        if (destinationMustBeWritable && destinationAcl?.Writable != true)
        {
            throw new AccessValidationException(
                "path_not_writable",
                "The current administrator cannot write to the destination directory.",
                new { path = destinationBase });
        }

        return new ValidatedTaskRequest(
            request,
            sourcePath,
            destinationPath);
    }

    private static void EnsureAuthorized(string path, IReadOnlyList<string> roots)
    {
        if (!roots.Any(root => PathSafety.IsSameOrDescendantPath(path, root)))
        {
            throw new AccessValidationException(
                "path_not_authorized",
                "The path is outside the folders authorized to ClipPort.",
                new { path });
        }
    }

    private static FnOsAclResult? FindAcl(
        IEnumerable<FnOsAclResult> values,
        string path) =>
        values.FirstOrDefault(item => PathSemantics.Comparer.Equals(
            NormalizePath(item.Path),
            path));

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            throw new AccessValidationException("invalid_request", "The path is invalid.");
        }
    }

    private static bool TryNormalizePath(string path, out string normalized)
    {
        try
        {
            normalized = Path.GetFullPath(path);
            return Path.IsPathFullyQualified(normalized);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    private static bool TryNormalizeAcl(FnOsAclResult item, out FnOsAclResult? normalized)
    {
        if (!TryNormalizePath(item.Path, out string normalizedPath))
        {
            normalized = null;
            return false;
        }
        normalized = item with { Path = normalizedPath };
        return true;
    }

    private static string NormalizeLanguage(string language) =>
        language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";
}
