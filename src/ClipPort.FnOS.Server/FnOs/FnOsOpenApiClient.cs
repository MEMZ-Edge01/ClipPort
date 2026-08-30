using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;

namespace ClipPort.FnOS.FnOs;

public sealed class FnOsOpenApiClient : IFnOsOpenApi, IDisposable
{
    internal const string DefaultSocketPath = "/var/run/trim_open_gateway_apiscope.socket";
    private readonly HttpClient _client;
    private readonly string _appName;

    public FnOsOpenApiClient()
        : this(
            Environment.GetEnvironmentVariable("CLIPPORT_FNOS_API_SOCKET") ?? DefaultSocketPath,
            Environment.GetEnvironmentVariable("TRIM_APPNAME") ?? "clipport")
    {
    }

    internal FnOsOpenApiClient(string socketPath, string appName, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(
                    AddressFamily.Unix,
                    SocketType.Stream,
                    ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(
                        new UnixDomainSocketEndPoint(socketPath),
                        cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        _client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost"),
            Timeout = timeout ?? TimeSpan.FromSeconds(15)
        };
        _appName = appName;
    }

    public async Task<IReadOnlyList<string>> GetSharedAccessibleFoldersAsync(
        CancellationToken cancellationToken)
    {
        JsonElement data = await CallAsync(
            "trim.file.getSharedAccessibleFolders",
            new { },
            cancellationToken);
        if (!data.TryGetProperty("paths", out JsonElement paths) ||
            paths.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return paths.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();
    }

    public async Task<IReadOnlyList<FnOsAclResult>> CheckUserAclAsync(
        int userId,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        JsonElement data = await CallAsync(
            "trim.file.checkUserACL",
            new { uid = userId, path = paths },
            cancellationToken);
        if (data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<FnOsAclResult>();
        foreach (JsonElement item in data.EnumerateArray())
        {
            string? path = GetString(item, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            results.Add(new FnOsAclResult(
                path,
                GetBoolean(item, "readable"),
                GetBoolean(item, "writable"),
                GetBoolean(item, "deletable")));
        }
        return results;
    }

    public async Task<IReadOnlyDictionary<string, string>> ConvertPathsAsync(
        IReadOnlyList<string> paths,
        string language,
        CancellationToken cancellationToken)
    {
        JsonElement data = await CallAsync(
            "trim.file.convertPath",
            new { path = paths, language },
            cancellationToken);
        if (!data.TryGetProperty("result", out JsonElement result) ||
            result.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, string>();
        }

        var converted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement item in result.EnumerateArray())
        {
            string? path = GetString(item, "path");
            string? semanticPath = GetString(item, "semanticPath");
            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(semanticPath))
            {
                converted[path] = semanticPath;
            }
        }
        return converted;
    }

    public async Task DeleteSharedAccessibleFolderAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await CallAsync(
            "trim.file.delSharedAccessibleFolder",
            new { path },
            cancellationToken);
    }

    private async Task<JsonElement> CallAsync(
        string operation,
        object data,
        CancellationToken cancellationToken)
    {
        string? token = Environment.GetEnvironmentVariable("TRIM_API_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new FnOsOpenApiException(
                "fnos_token_missing",
                "fnOS did not provide the application API credential.",
                operation);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/trimapp")
        {
            Content = JsonContent.Create(new
            {
                reqId = Guid.NewGuid().ToString("N"),
                req = operation,
                appName = _appName,
                data
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FnOsOpenApiException(
                "fnos_timeout",
                $"fnOS operation {operation} timed out.",
                operation);
        }
        catch (HttpRequestException ex)
        {
            throw new FnOsOpenApiException(
                "fnos_transport_error",
                $"fnOS operation {operation} could not reach the system service.",
                operation,
                innerException: ex);
        }

        using (response)
        {
            JsonDocument? document = null;
            try
            {
                document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
            }
            catch (JsonException ex)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw CreateHttpException(operation, response.StatusCode, null, null, ex);
                }
                throw new FnOsOpenApiException(
                    "fnos_invalid_response",
                    $"fnOS operation {operation} returned invalid JSON.",
                    operation,
                    (int)response.StatusCode,
                    innerException: ex);
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                string? requestId = GetString(root, "reqId");
                int code = root.TryGetProperty("code", out JsonElement codeElement) &&
                           codeElement.TryGetInt32(out int parsedCode)
                    ? parsedCode
                    : -1;
                string? gatewayMessage = GetString(root, "msg");
                if (!response.IsSuccessStatusCode)
                {
                    throw CreateHttpException(
                        operation,
                        response.StatusCode,
                        code >= 0 ? code : null,
                        requestId,
                        message: gatewayMessage);
                }
                if (code < 0)
                {
                    throw new FnOsOpenApiException(
                        "fnos_invalid_response",
                        $"fnOS operation {operation} returned no business status.",
                        operation,
                        (int)response.StatusCode,
                        requestId);
                }
                if (code != 0)
                {
                    string message = gatewayMessage ?? $"fnOS operation {operation} failed.";
                    throw new FnOsOpenApiException(
                        $"fnos_api_{code}",
                        message,
                        operation,
                        (int)response.StatusCode,
                        requestId);
                }

                return root.TryGetProperty("data", out JsonElement result)
                    ? result.Clone()
                    : JsonDocument.Parse("{}").RootElement.Clone();
            }
        }
    }

    private static FnOsOpenApiException CreateHttpException(
        string operation,
        HttpStatusCode statusCode,
        int? businessCode,
        string? requestId,
        Exception? innerException = null,
        string? message = null) =>
        new(
            businessCode is >= 0 ? $"fnos_api_{businessCode}" : $"fnos_http_{(int)statusCode}",
            string.IsNullOrWhiteSpace(message)
                ? $"fnOS operation {operation} was rejected with HTTP {(int)statusCode}."
                : message,
            operation,
            (int)statusCode,
            requestId,
            innerException);

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool GetBoolean(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        property.GetBoolean();

    public void Dispose() => _client.Dispose();
}
