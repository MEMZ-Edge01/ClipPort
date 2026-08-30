using System.Text.Json;
using System.Text.Json.Serialization;
using ClipPort.FnOS.Api;
using ClipPort.FnOS.FnOs;
using ClipPort.FnOS.Infrastructure;
using ClipPort.FnOS.Persistence;
using ClipPort.FnOS.Realtime;
using ClipPort.FnOS.Security;
using ClipPort.FnOS.Settings;
using ClipPort.FnOS.Tasks;
using ClipPort.FnOS.Updates;
using ClipPort.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.DataProtection;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string dataDirectory = Environment.GetEnvironmentVariable("TRIM_PKGVAR") ??
    Path.Combine(AppContext.BaseDirectory, ".clipport-data");
string reportsDirectory = Path.Combine(dataDirectory, "reports");
string socketPath = Environment.GetEnvironmentVariable("CLIPPORT_LISTEN_SOCKET") ??
    Path.Combine(dataDirectory, "clipport.sock");
Directory.CreateDirectory(dataDirectory);
Directory.CreateDirectory(reportsDirectory);
string keyDirectory = Path.Combine(dataDirectory, "keys");
Directory.CreateDirectory(keyDirectory);
if (OperatingSystem.IsLinux())
{
    File.SetUnixFileMode(
        keyDirectory,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}

if (OperatingSystem.IsLinux() && !builder.Environment.IsDevelopment())
{
    if (File.Exists(socketPath))
    {
        File.Delete(socketPath);
    }
    builder.WebHost.ConfigureKestrel(options =>
        options.ListenUnixSocket(socketPath, listen =>
            listen.Protocols = HttpProtocols.Http1));
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddSingleton(new JsonPartialFileJournal(dataDirectory));
builder.Services.AddSingleton<IPartialFileJournal>(services =>
    services.GetRequiredService<JsonPartialFileJournal>());
builder.Services.AddSingleton<FileCopyService>();
builder.Services.AddSingleton(new FnOsTaskStore(dataDirectory));
builder.Services.AddSingleton(new JobHistoryService(dataDirectory, reportsDirectory));
builder.Services.AddSingleton<CsrfTokenStore>();
builder.Services.AddSingleton<TaskEventHub>();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
    .SetApplicationName("ClipPort.FnOS");
builder.Services.AddSingleton(services => new FnOsSettingsStore(
    dataDirectory,
    services.GetRequiredService<IDataProtectionProvider>()));
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddHttpClient<FnOsUpdateService>(client =>
    client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<AuthorizedFolderModule>();
builder.Services.AddSingleton<FnOsTaskManager>();
builder.Services.AddHostedService(services => services.GetRequiredService<FnOsTaskManager>());

string? developmentFolders = Environment.GetEnvironmentVariable("CLIPPORT_DEV_AUTHORIZED_PATHS");
if (builder.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(developmentFolders))
{
    builder.Services.AddSingleton<IFnOsOpenApi>(
        new DevelopmentFnOsOpenApi(developmentFolders));
}
else
{
    builder.Services.AddSingleton<IFnOsOpenApi, FnOsOpenApiClient>();
}

WebApplication app = builder.Build();
app.UsePathBase("/app/clipport");
app.UseRouting();
app.Use(async (context, next) =>
{
    if (context.Request.PathBase == "/app/clipport" && !context.Request.Path.HasValue)
    {
        context.Response.Redirect("/app/clipport/");
        return;
    }
    await next(context);
});
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseMiddleware<GatewaySecurityMiddleware>();
app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapClipPortApi();
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
