using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClipPort.FnOS.Contracts;
using ClipPort.Models;
using ClipPort.FnOS.Settings;
using Microsoft.AspNetCore.TestHost;

namespace ClipPort.FnOS.Tests;

public sealed class ApiContractTests
{
    [Fact]
    public async Task ApiRequiresAdministratorGatewayIdentity()
    {
        using var factory = new ClipPortWebFactory();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/session");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("admin_required", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    [Fact]
    public async Task SessionAndFoldersDoNotExposeSystemToken()
    {
        using var factory = new ClipPortWebFactory();
        using HttpClient client = factory.CreateClient();
        using var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/session");
        ClipPortWebFactory.AddAdminHeaders(sessionRequest);

        HttpResponseMessage sessionResponse = await client.SendAsync(sessionRequest);
        string sessionJson = await sessionResponse.Content.ReadAsStringAsync();
        SessionResponse? session = JsonSerializer.Deserialize<SessionResponse>(
            sessionJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
        Assert.True(session?.IsCompatible);
        Assert.DoesNotContain(factory.Api.TokenCanary, sessionJson, StringComparison.Ordinal);

        using var foldersRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/authorized-folders");
        ClipPortWebFactory.AddAdminHeaders(foldersRequest);
        string foldersJson = await (await client.SendAsync(foldersRequest)).Content.ReadAsStringAsync();
        Assert.Contains("共享文件/测试", foldersJson, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Api.TokenCanary, foldersJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthorizedFoldersTolerateDuplicateNormalizedAclRows()
    {
        using var factory = new ClipPortWebFactory();
        using HttpClient client = factory.CreateClient();
        factory.Api.DuplicateAclRows = true;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/authorized-folders");
        ClipPortWebFactory.AddAdminHeaders(request);

        HttpResponseMessage response = await client.SendAsync(request);
        AuthorizedFolderDto[]? folders = await response.Content.ReadFromJsonAsync<AuthorizedFolderDto[]>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(folders!);
        Assert.True(folders![0].Readable);
        Assert.True(folders[0].Writable);
    }

    [Fact]
    public async Task RevokingAuthorizationRequiresCsrfAndOnlyAcceptsAnAuthorizedRoot()
    {
        using var factory = new ClipPortWebFactory();
        using HttpClient client = factory.CreateClient();
        SessionResponse session = await GetSessionAsync(client);

        using var rejected = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/authorized-folders")
        {
            Content = JsonContent.Create(new RevokeAuthorizedFolderRequest(factory.SourcePath))
        };
        ClipPortWebFactory.AddAdminHeaders(rejected);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(rejected)).StatusCode);

        using var invalid = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/authorized-folders")
        {
            Content = JsonContent.Create(new RevokeAuthorizedFolderRequest(factory.SourcePath))
        };
        ClipPortWebFactory.AddAdminHeaders(invalid);
        invalid.Headers.Add("X-ClipPort-CSRF", session.CsrfToken);
        HttpResponseMessage invalidResponse = await client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        string root = Directory.GetParent(factory.SourcePath)!.FullName;
        using var accepted = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/authorized-folders")
        {
            Content = JsonContent.Create(new RevokeAuthorizedFolderRequest(root))
        };
        ClipPortWebFactory.AddAdminHeaders(accepted);
        accepted.Headers.Add("X-ClipPort-CSRF", session.CsrfToken);
        HttpResponseMessage acceptedResponse = await client.SendAsync(accepted);

        Assert.Equal(HttpStatusCode.NoContent, acceptedResponse.StatusCode);
        Assert.Equal(Path.GetFullPath(root), Assert.Single(factory.Api.RevokedPaths));
    }

    [Fact]
    public async Task WriteEndpointsRequireCsrfAndCreateValidatedTask()
    {
        using var factory = new ClipPortWebFactory();
        using HttpClient client = factory.CreateClient();
        CreateTaskRequest task = CreateRequest(factory) with { IsPriority = true };

        using var rejected = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tasks")
        {
            Content = JsonContent.Create(task)
        };
        ClipPortWebFactory.AddAdminHeaders(rejected);
        HttpResponseMessage rejectedResponse = await client.SendAsync(rejected);
        Assert.Equal(HttpStatusCode.Forbidden, rejectedResponse.StatusCode);

        SessionResponse session = await GetSessionAsync(client);
        using var accepted = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tasks")
        {
            Content = JsonContent.Create(task)
        };
        ClipPortWebFactory.AddAdminHeaders(accepted);
        accepted.Headers.Add("X-ClipPort-CSRF", session.CsrfToken);
        HttpResponseMessage acceptedResponse = await client.SendAsync(accepted);

        Assert.Equal(HttpStatusCode.Created, acceptedResponse.StatusCode);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        FnOsTaskRecord? record = await acceptedResponse.Content.ReadFromJsonAsync<FnOsTaskRecord>(jsonOptions);
        Assert.Equal(Path.GetFullPath(factory.SourcePath), record?.Request.SourcePath);
        Assert.True(record?.Request.IsPriority);
    }

    [Fact]
    public async Task SettingsApiEncryptsAndNeverReturnsNotificationSecrets()
    {
        using var factory = new ClipPortWebFactory();
        using HttpClient client = factory.CreateClient();
        SessionResponse session = await GetSessionAsync(client);
        var settings = new SaveFnOsSettingsRequest(
            AppThemeMode.Dark,
            AppAccentMode.Mint,
            AppLanguage.English,
            null,
            true,
            true,
            [new FnOsNotificationChannelUpdate(
                "channel",
                "webhook",
                NotificationChannelKind.Feishu,
                true,
                "https://example.invalid/hook/api-secret-canary",
                false,
                string.Empty,
                465,
                string.Empty,
                "smtp-secret-canary",
                false,
                string.Empty,
                string.Empty)]);
        using var save = new HttpRequestMessage(HttpMethod.Put, "/api/v1/settings")
        {
            Content = JsonContent.Create(settings)
        };
        ClipPortWebFactory.AddAdminHeaders(save);
        save.Headers.Add("X-ClipPort-CSRF", session.CsrfToken);

        string saveJson = await (await client.SendAsync(save)).Content.ReadAsStringAsync();
        using var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/settings");
        ClipPortWebFactory.AddAdminHeaders(get);
        string getJson = await (await client.SendAsync(get)).Content.ReadAsStringAsync();
        string storageJson = await File.ReadAllTextAsync(
            Path.Combine(Directory.GetParent(factory.SourcePath)!.FullName, "settings.json"));

        Assert.DoesNotContain("api-secret-canary", saveJson + getJson + storageJson, StringComparison.Ordinal);
        Assert.DoesNotContain("smtp-secret-canary", saveJson + getJson + storageJson, StringComparison.Ordinal);
        Assert.Contains("\"hasEndpoint\":true", getJson, StringComparison.Ordinal);
        Assert.Contains("fnosdp:", storageJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchReportExportRechecksAuthorizationAndWritesSelectedReports()
    {
        using var factory = new ClipPortWebFactory();
        using HttpClient client = factory.CreateClient();
        SessionResponse session = await GetSessionAsync(client);
        CreateTaskRequest create = CreateRequest(factory) with { Mode = FnOsTaskMode.CopyOnly };
        using var createMessage = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tasks")
        {
            Content = JsonContent.Create(create)
        };
        ClipPortWebFactory.AddAdminHeaders(createMessage);
        createMessage.Headers.Add("X-ClipPort-CSRF", session.CsrfToken);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        FnOsTaskRecord created = (await (await client.SendAsync(createMessage)).Content
            .ReadFromJsonAsync<FnOsTaskRecord>(jsonOptions))!;
        FnOsTaskRecord finished = created;
        for (int attempt = 0; attempt < 50 && string.IsNullOrWhiteSpace(finished.ReportFileName); attempt++)
        {
            await Task.Delay(20);
            using var taskRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tasks/{created.Id}");
            ClipPortWebFactory.AddAdminHeaders(taskRequest);
            finished = (await (await client.SendAsync(taskRequest)).Content
                .ReadFromJsonAsync<FnOsTaskRecord>(jsonOptions))!;
        }
        Assert.False(string.IsNullOrWhiteSpace(finished.ReportFileName));

        string exportDirectory = Directory.GetParent(factory.SourcePath)!.FullName;
        using var export = new HttpRequestMessage(HttpMethod.Post, "/api/v1/reports/export")
        {
            Content = JsonContent.Create(new BatchReportExportRequest([created.Id], exportDirectory))
        };
        ClipPortWebFactory.AddAdminHeaders(export);
        export.Headers.Add("X-ClipPort-CSRF", session.CsrfToken);
        HttpResponseMessage response = await client.SendAsync(export);
        BatchReportExportResponse? result = await response.Content.ReadFromJsonAsync<BatchReportExportResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, result?.ExportedCount);
        Assert.True(File.Exists(Path.Combine(exportDirectory, Assert.Single(result!.FileNames))));

        using var unauthorized = new HttpRequestMessage(HttpMethod.Post, "/api/v1/reports/export")
        {
            Content = JsonContent.Create(new BatchReportExportRequest([created.Id], Path.GetPathRoot(exportDirectory)!))
        };
        ClipPortWebFactory.AddAdminHeaders(unauthorized);
        unauthorized.Headers.Add("X-ClipPort-CSRF", session.CsrfToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(unauthorized)).StatusCode);
    }

    [Fact]
    public async Task AuthorizationAndAclFailuresUseStableCodes()
    {
        using var factory = new ClipPortWebFactory();
        using HttpClient client = factory.CreateClient();
        SessionResponse session = await GetSessionAsync(client);
        CreateTaskRequest outside = CreateRequest(factory) with
        {
            SourcePath = Path.GetPathRoot(factory.SourcePath)!
        };

        ErrorResponse unauthorized = await PostExpectErrorAsync(client, session, outside);
        Assert.Equal("path_not_authorized", unauthorized.Code);

        factory.Api.Writable = false;
        ErrorResponse readonlyDestination = await PostExpectErrorAsync(
            client,
            session,
            CreateRequest(factory));
        Assert.Equal("path_not_writable", readonlyDestination.Code);
    }

    [Fact]
    public async Task WebSocketReconnectAlwaysStartsWithAFullSnapshot()
    {
        using var factory = new ClipPortWebFactory();
        using HttpClient _ = factory.CreateClient();

        string first = await ConnectAndReadFirstEventAsync(factory);
        string reconnected = await ConnectAndReadFirstEventAsync(factory);

        Assert.Contains("\"type\":\"snapshot\"", first, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"snapshot\"", reconnected, StringComparison.Ordinal);
    }

    private static CreateTaskRequest CreateRequest(ClipPortWebFactory factory) => new(
        FnOsTaskMode.CopyAndVerify,
        factory.SourcePath,
        factory.DestinationPath,
        null,
        ExistingFilePolicy.Overwrite,
        VerificationAlgorithmKind.Sha256,
        VerificationExecutionMode.AfterCopy);

    private static async Task<SessionResponse> GetSessionAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/session");
        ClipPortWebFactory.AddAdminHeaders(request);
        return (await (await client.SendAsync(request)).Content.ReadFromJsonAsync<SessionResponse>())!;
    }

    private static async Task<ErrorResponse> PostExpectErrorAsync(
        HttpClient client,
        SessionResponse session,
        CreateTaskRequest requestBody)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tasks")
        {
            Content = JsonContent.Create(requestBody)
        };
        ClipPortWebFactory.AddAdminHeaders(request);
        request.Headers.Add("X-ClipPort-CSRF", session.CsrfToken);
        HttpResponseMessage response = await client.SendAsync(request);
        return (await response.Content.ReadFromJsonAsync<ErrorResponse>())!;
    }

    private static async Task<string> ConnectAndReadFirstEventAsync(ClipPortWebFactory factory)
    {
        WebSocketClient client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["X-Trim-Userid"] = "1000";
            request.Headers["X-Trim-Username"] = "admin";
            request.Headers["X-Trim-Isadmin"] = "true";
        };
        using WebSocket socket = await client.ConnectAsync(
            new Uri("ws://localhost/ws"),
            CancellationToken.None);
        byte[] buffer = new byte[8192];
        WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        string payload = Encoding.UTF8.GetString(buffer, 0, result.Count);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", CancellationToken.None);
        return payload;
    }
}
