using System.Net;
using System.Text;
using ClipPort.FnOS.Updates;

namespace ClipPort.FnOS.Tests;

public sealed class FnOsUpdateServiceTests
{
    [Fact]
    public async Task SelectsOnlyTheFnOsX86FpkAsset()
    {
        const string response = """
            {
              "tag_name":"v9.0.0",
              "html_url":"https://github.example/release",
              "published_at":"2026-08-30T00:00:00Z",
              "assets":[
                {"name":"ClipPort-win-x64.zip","browser_download_url":"https://download/win"},
                {"name":"ClipPort-9.0.0-fnos-x86.fpk","browser_download_url":"https://download/fnos"}
              ]
            }
            """;
        using var client = new HttpClient(new StubHandler(response));
        var service = new FnOsUpdateService(client);

        FnOsUpdateMetadata result = await service.CheckAsync(CancellationToken.None);

        Assert.True(result.UpdateAvailable);
        Assert.Equal("ClipPort-9.0.0-fnos-x86.fpk", result.AssetName);
        Assert.Equal("https://download/fnos", result.DownloadUrl);
    }

    [Theory]
    [InlineData("v0.9.0", "ClipPort-0.9.0-fnos-x86.fpk", false)]
    [InlineData("v9.0.0", "ClipPort-9.0.0-win-x64.zip", false)]
    public async Task OffersOnlyANewerReleaseThatContainsAFnOsPackage(
        string tag,
        string assetName,
        bool expected)
    {
        string response = $$"""
            {
              "tag_name":"{{tag}}",
              "html_url":"https://github.example/release",
              "assets":[{"name":"{{assetName}}","browser_download_url":"https://download/asset"}]
            }
            """;
        using var client = new HttpClient(new StubHandler(response));

        FnOsUpdateMetadata result = await new FnOsUpdateService(client)
            .CheckAsync(CancellationToken.None);

        Assert.Equal(expected, result.UpdateAvailable);
    }

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
    }
}
