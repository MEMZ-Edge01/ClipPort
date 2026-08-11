using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ClipPort.Models;

namespace ClipPort.Services;

/// <summary>
/// 通过 GitHub Releases API 检查、下载并启动应用更新。
/// 发布包是便携目录 zip（含主程序与 ClipPort.Updater.exe），
/// 更新流程由独立的更新器进程完成，主程序下载校验后即可安全退出。
/// </summary>
public sealed class UpdateService
{
    private const string RepositoryOwner = "MEMZ-Edge01";
    private const string RepositoryName = "ClipPort";
    private const string UpdaterFileName = "ClipPort.Updater.exe";

    private static readonly string UpdateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipPort",
        "Updates");

    private readonly HttpClient _httpClient;

    public UpdateService()
    {
        _httpClient = new HttpClient
        {
            // 下载 150MB 左右的发布包可能较慢，超时放宽到 10 分钟。
            Timeout = TimeSpan.FromMinutes(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ClipPort-Updater/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>
    /// 返回 GitHub 上最新的（含预发布）可更新 release；
    /// 找不到匹配资产时返回 null。
    /// </summary>
    public async Task<GitHubRelease?> GetLatestReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        string requestUrl =
            $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases";
        using HttpResponseMessage response = await _httpClient.GetAsync(
            requestUrl,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        List<GitHubRelease>? releases =
            JsonSerializer.Deserialize<List<GitHubRelease>>(json);
        if (releases is null)
        {
            return null;
        }

        // API 已按发布时间倒序返回。beta 是 prerelease，因此必须遍历
        // 完整列表而不是使用 /releases/latest（它只返回正式版）。
        return releases.FirstOrDefault(release =>
            !release.IsDraft &&
            release.Assets.Any(asset =>
                IsZipAsset(asset) &&
                asset.DownloadUrl is not null) &&
            release.Assets.Any(asset => IsSha256Asset(release, asset)));
    }

    /// <summary>
    /// 下载更新包到本地，并用 release 自带的 .sha256 资产校验完整性。
    /// 返回 zip 文件的完整路径。
    /// </summary>
    public async Task<string> DownloadUpdateAsync(
        GitHubRelease release,
        GitHubReleaseAsset zipAsset,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(UpdateDirectory);
        string destinationPath = Path.Combine(UpdateDirectory, zipAsset.Name!);
        string partPath = destinationPath + ".part";

        using (HttpResponseMessage response = await _httpClient.GetAsync(
                   zipAsset.DownloadUrl,
                   HttpCompletionOption.ResponseHeadersRead,
                   cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            long totalBytes = response.Content.Headers.ContentLength ?? zipAsset.Size;
            await using Stream content = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            await using FileStream output = File.Create(partPath);

            var buffer = new byte[81920];
            long writtenBytes = 0;
            int readBytes;
            while ((readBytes = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, readBytes), cancellationToken);
                writtenBytes += readBytes;
                if (totalBytes > 0)
                {
                    progress?.Report((double)writtenBytes / totalBytes * 100);
                }
            }
        }

        string? sha256Asset = release.Assets
            .FirstOrDefault(asset => IsSha256Asset(release, asset))
            ?.DownloadUrl;
        if (string.IsNullOrWhiteSpace(sha256Asset))
        {
            File.Delete(partPath);
            throw new InvalidOperationException("发布包缺少 SHA-256 校验文件，已取消更新。");
        }

        string expectedHash = await DownloadSha256Async(
            sha256Asset,
            cancellationToken);
        string actualHash = await ComputeFileSha256Async(
            partPath,
            cancellationToken);
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partPath);
            throw new IOException(
                $"更新包校验失败（期望 {expectedHash}，实际 {actualHash}），已取消更新。");
        }

        File.Move(partPath, destinationPath, overwrite: true);
        return destinationPath;
    }

    /// <summary>
    /// 从应用目录把更新器复制到 %LOCALAPPDATA% 后启动它，避免替换
    /// 应用目录时更新器自身被占用；随后主程序应立即退出。
    /// </summary>
    public void LaunchUpdater(string zipPath, string targetDirectory)
    {
        string updaterSource = Path.Combine(
            AppContext.BaseDirectory,
            UpdaterFileName);
        if (!File.Exists(updaterSource))
        {
            throw new FileNotFoundException("找不到更新器组件，无法完成更新。", updaterSource);
        }

        Directory.CreateDirectory(UpdateDirectory);
        string updaterCopyPath = Path.Combine(UpdateDirectory, UpdaterFileName);
        File.Copy(updaterSource, updaterCopyPath, overwrite: true);

        var startInfo = new ProcessStartInfo(updaterCopyPath)
        {
            WorkingDirectory = UpdateDirectory,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--source");
        startInfo.ArgumentList.Add(zipPath);
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(targetDirectory);
        startInfo.ArgumentList.Add("--main-exe");
        startInfo.ArgumentList.Add("ClipPort.exe");
        startInfo.ArgumentList.Add("--wait-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("--restart");

        Process.Start(startInfo);
    }

    /// <summary>
    /// 比较远端版本是否比当前版本新（支持 v1.2.3-beta 这类语义化版本）。
    /// </summary>
    public static bool IsNewerVersion(string? currentVersion, string? latestVersion)
    {
        return TryCompareVersions(currentVersion, latestVersion, out int comparison) &&
               comparison < 0;
    }

    public static string GetCurrentVersion()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // 去掉可能的 "+commit" 后缀，只保留版本部分。
            int plusIndex = informational.IndexOf('+');
            return plusIndex >= 0 ? informational[..plusIndex] : informational;
        }
        return assembly.GetName().Version?.ToString() ?? "1.0.0-beta";
    }

    public static bool IsZipAsset(GitHubReleaseAsset asset) =>
        asset.Name is not null &&
        asset.Name.StartsWith("ClipPort-", StringComparison.OrdinalIgnoreCase) &&
        asset.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256Asset(GitHubRelease release, GitHubReleaseAsset asset) =>
        asset.Name is not null &&
        release.Assets.Any(zipAsset =>
            IsZipAsset(zipAsset) &&
            string.Equals(
                asset.Name,
                zipAsset.Name + ".sha256",
                StringComparison.OrdinalIgnoreCase));

    private async Task<string> DownloadSha256Async(
        string url,
        CancellationToken cancellationToken)
    {
        string sha256 = await _httpClient.GetStringAsync(url, cancellationToken);
        return sha256.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty;
    }

    private static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static bool TryCompareVersions(
        string? left,
        string? right,
        out int comparison)
    {
        comparison = 0;
        if (!TryParseVersion(left, out VersionParts? leftParts) ||
            !TryParseVersion(right, out VersionParts? rightParts))
        {
            return false;
        }

        comparison = leftParts!.CompareCore(rightParts!);
        if (comparison == 0)
        {
            comparison = leftParts.ComparePrerelease(rightParts!);
        }
        return true;
    }

    private static bool TryParseVersion(string? value, out VersionParts? parts)
    {
        parts = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        int dashIndex = text.IndexOf('-');
        string core = dashIndex >= 0 ? text[..dashIndex] : text;
        string? prerelease = dashIndex >= 0 ? text[(dashIndex + 1)..] : null;

        string[] segments = core.Split('.');
        if (segments.Length is < 1 or > 3 ||
            !int.TryParse(segments[0], out int major) ||
            !int.TryParse(segments.Length > 1 ? segments[1] : "0", out int minor) ||
            !int.TryParse(segments.Length > 2 ? segments[2] : "0", out int patch))
        {
            return false;
        }

        parts = new VersionParts(major, minor, patch, prerelease);
        return true;
    }

    private sealed record VersionParts(int Major, int Minor, int Patch, string? Prerelease)
    {
        public int CompareCore(VersionParts other)
        {
            int result = Major.CompareTo(other.Major);
            if (result != 0)
            {
                return result;
            }
            result = Minor.CompareTo(other.Minor);
            if (result != 0)
            {
                return result;
            }
            return Patch.CompareTo(other.Patch);
        }

        public int ComparePrerelease(VersionParts other)
        {
            // 正式版优先于任何预发布版本。
            bool leftHasPre = !string.IsNullOrEmpty(Prerelease);
            bool rightHasPre = !string.IsNullOrEmpty(other.Prerelease);
            if (leftHasPre != rightHasPre)
            {
                return leftHasPre ? -1 : 1;
            }
            if (!leftHasPre)
            {
                return 0;
            }

            string[] leftParts = Prerelease!.Split('.');
            string[] rightParts = other.Prerelease!.Split('.');
            int count = Math.Min(leftParts.Length, rightParts.Length);
            for (int index = 0; index < count; index++)
            {
                int result = ComparePrereleaseSegment(leftParts[index], rightParts[index]);
                if (result != 0)
                {
                    return result;
                }
            }
            return leftParts.Length.CompareTo(rightParts.Length);
        }

        private static int ComparePrereleaseSegment(string left, string right)
        {
            bool leftIsNumber = int.TryParse(left, out int leftNumber);
            bool rightIsNumber = int.TryParse(right, out int rightNumber);
            if (leftIsNumber && rightIsNumber)
            {
                return leftNumber.CompareTo(rightNumber);
            }
            if (leftIsNumber != rightIsNumber)
            {
                // 语义化版本规范：数字标识符优先级低于字母标识符。
                return leftIsNumber ? -1 : 1;
            }
            return string.CompareOrdinal(left, right);
        }
    }
}
