using ClipPort.FnOS.FnOs;
using ClipPort.FnOS.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClipPort.FnOS.Tests;

internal sealed class ClipPortWebFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "clipport-fnos-tests-" + Guid.NewGuid().ToString("N"));

    public FakeFnOsOpenApi Api { get; private set; } = null!;
    public string SourcePath => Path.Combine(_root, "source");
    public string DestinationPath => Path.Combine(_root, "destination");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(SourcePath);
        Directory.CreateDirectory(DestinationPath);
        Api = new FakeFnOsOpenApi(_root);
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFnOsOpenApi>();
            services.RemoveAll<FnOsSettingsStore>();
            services.AddSingleton<IFnOsOpenApi>(Api);
            services.AddSingleton(new FnOsSettingsStore(
                _root,
                new EphemeralDataProtectionProvider()));
        });
    }

    public static void AddAdminHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("X-Trim-Userid", "1000");
        request.Headers.Add("X-Trim-Username", "admin");
        request.Headers.Add("X-Trim-Isadmin", "true");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
