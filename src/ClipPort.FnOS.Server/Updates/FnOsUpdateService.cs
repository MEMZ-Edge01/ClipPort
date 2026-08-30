using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using ClipPort.Services;

namespace ClipPort.FnOS.Updates;

public sealed record FnOsUpdateMetadata(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string? AssetName,
    string? DownloadUrl,
    string ReleasePageUrl,
    DateTimeOffset? PublishedAt);

public sealed class FnOsUpdateService(HttpClient httpClient)
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/MEMZ-Edge01/ClipPort/releases/latest";

    public async Task<FnOsUpdateMetadata> CheckAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ClipPort-fnOS", CurrentVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        JsonElement root = document.RootElement;
        string? latest = GetString(root, "tag_name")?.TrimStart('v');
        string releasePage = GetString(root, "html_url") ??
            "https://github.com/MEMZ-Edge01/ClipPort/releases";
        JsonElement? asset = null;
        if (root.TryGetProperty("assets", out JsonElement assets) &&
            assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement candidate in assets.EnumerateArray())
            {
                string name = GetString(candidate, "name") ?? string.Empty;
                if (name.EndsWith(".fpk", StringComparison.OrdinalIgnoreCase) &&
                    (name.Contains("fnos-x86_64", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("fnos-x86", StringComparison.OrdinalIgnoreCase)))
                {
                    asset = candidate;
                    break;
                }
            }
        }
        string? publishedText = GetString(root, "published_at");
        DateTimeOffset? publishedAt = DateTimeOffset.TryParse(publishedText, out DateTimeOffset parsed)
            ? parsed
            : null;
        bool updateAvailable = asset is not null &&
            SemanticVersionComparer.IsNewer(CurrentVersion, latest);
        return new FnOsUpdateMetadata(
            CurrentVersion,
            latest,
            updateAvailable,
            asset is JsonElement selected ? GetString(selected, "name") : null,
            asset is JsonElement selectedAsset ? GetString(selectedAsset, "browser_download_url") : null,
            releasePage,
            publishedAt);
    }

    private static string CurrentVersion =>
        typeof(FnOsUpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "1.0.0-beta";

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
