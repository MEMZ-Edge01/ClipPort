namespace ClipPort.FnOS.FnOs;

public sealed record FnOsAclResult(
    string Path,
    bool Readable,
    bool Writable,
    bool Deletable);

public interface IFnOsOpenApi
{
    Task<IReadOnlyList<string>> GetSharedAccessibleFoldersAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FnOsAclResult>> CheckUserAclAsync(
        int userId,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> ConvertPathsAsync(
        IReadOnlyList<string> paths,
        string language,
        CancellationToken cancellationToken);

    Task DeleteSharedAccessibleFolderAsync(
        string path,
        CancellationToken cancellationToken);
}

public sealed class FnOsOpenApiException : Exception
{
    public FnOsOpenApiException(
        string code,
        string message,
        string operation = "startup",
        int? httpStatusCode = null,
        string? requestId = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Operation = operation;
        HttpStatusCode = httpStatusCode;
        RequestId = requestId;
    }

    public string Code { get; }
    public string Operation { get; }
    public int? HttpStatusCode { get; }
    public string? RequestId { get; }
}
