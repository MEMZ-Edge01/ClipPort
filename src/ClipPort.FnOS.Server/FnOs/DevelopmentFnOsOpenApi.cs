using ClipPort.Models;

namespace ClipPort.FnOS.FnOs;

/// <summary>
/// Local adapter for development and contract tests; production never enables
/// it because the variable is not present in the FPK lifecycle environment.
/// </summary>
public sealed class DevelopmentFnOsOpenApi : IFnOsOpenApi
{
    private readonly List<string> _paths;

    public DevelopmentFnOsOpenApi(string paths) =>
        _paths = paths.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .ToList();

    public Task<IReadOnlyList<string>> GetSharedAccessibleFoldersAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(_paths.ToArray());

    public Task<IReadOnlyList<FnOsAclResult>> CheckUserAclAsync(
        int userId,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FnOsAclResult>>(
            paths.Select(path => new FnOsAclResult(path, true, true, true)).ToArray());

    public Task<IReadOnlyDictionary<string, string>> ConvertPathsAsync(
        IReadOnlyList<string> paths,
        string language,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(
            paths.ToDictionary(path => path, path => path, StringComparer.Ordinal));

    public Task DeleteSharedAccessibleFolderAsync(
        string path,
        CancellationToken cancellationToken)
    {
        _paths.RemoveAll(value => PathSemantics.Comparer.Equals(value, Path.GetFullPath(path)));
        return Task.CompletedTask;
    }

}
