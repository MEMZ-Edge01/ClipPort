using ClipPort.FnOS.FnOs;

namespace ClipPort.FnOS.Tests;

internal sealed class FakeFnOsOpenApi(string root) : IFnOsOpenApi
{
    public bool Readable { get; set; } = true;
    public bool Writable { get; set; } = true;
    public bool DuplicateAclRows { get; set; }
    public string TokenCanary { get; } = "must-never-reach-http";
    public List<string> RevokedPaths { get; } = [];

    public Task<IReadOnlyList<string>> GetSharedAccessibleFoldersAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([root]);

    public Task<IReadOnlyList<FnOsAclResult>> CheckUserAclAsync(
        int userId,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FnOsAclResult>>(
            paths.SelectMany(path => Enumerable.Repeat(new FnOsAclResult(
                path,
                Readable,
                Writable,
                Writable), DuplicateAclRows ? 2 : 1)).ToArray());

    public Task<IReadOnlyDictionary<string, string>> ConvertPathsAsync(
        IReadOnlyList<string> paths,
        string language,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(
            paths.ToDictionary(path => path, _ => "共享文件/测试", StringComparer.Ordinal));

    public Task DeleteSharedAccessibleFolderAsync(
        string path,
        CancellationToken cancellationToken)
    {
        RevokedPaths.Add(path);
        return Task.CompletedTask;
    }

}
