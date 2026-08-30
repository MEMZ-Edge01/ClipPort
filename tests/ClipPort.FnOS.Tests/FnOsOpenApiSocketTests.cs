using System.Net.Sockets;
using System.Net;
using System.Text;
using ClipPort.FnOS.FnOs;

namespace ClipPort.FnOS.Tests;

public sealed class FnOsOpenApiSocketTests
{
    [Fact]
    public async Task ClientUsesUnixSocketAndReadsTheCurrentTokenForEveryCall()
    {
        if (!OperatingSystem.IsLinux())
        {
            // fnOS and the CI package job run on Linux. Windows AF_UNIX path
            // handling is not compatible enough for this transport-level test.
            return;
        }

        string socketPath = Path.Combine(
            Path.GetTempPath(),
            $"cp-{Guid.NewGuid():N}"[..20] + ".sock");
        string? originalToken = Environment.GetEnvironmentVariable("TRIM_API_TOKEN");
        Socket? listener = null;
        try
        {
            listener = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(2);
            using var client = new FnOsOpenApiClient(socketPath, "clipport");

            Environment.SetEnvironmentVariable("TRIM_API_TOKEN", "first-process-token");
            Task<string> firstRequest = ReplyOnceAsync(listener, "/vol1/share");
            Assert.Equal(
                ["/vol1/share"],
                await client.GetSharedAccessibleFoldersAsync(CancellationToken.None));
            Assert.Contains("Authorization: Bearer first-process-token", await firstRequest, StringComparison.OrdinalIgnoreCase);

            Environment.SetEnvironmentVariable("TRIM_API_TOKEN", "rotated-process-token");
            Task<string> secondRequest = ReplyOnceAsync(listener, "/vol2/media");
            Assert.Equal(
                ["/vol2/media"],
                await client.GetSharedAccessibleFoldersAsync(CancellationToken.None));
            Assert.Contains("Authorization: Bearer rotated-process-token", await secondRequest, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener?.Dispose();
            Environment.SetEnvironmentVariable("TRIM_API_TOKEN", originalToken);
            if (File.Exists(socketPath))
            {
                try
                {
                    File.Delete(socketPath);
                }
                catch (IOException) when (OperatingSystem.IsWindows())
                {
                    // Windows owns the AF_UNIX namespace entry until process exit.
                }
            }
        }
    }

    [Theory]
    [InlineData(403, "{\"reqId\":\"trace-403\",\"code\":200003,\"msg\":\"Forbidden\",\"data\":{}}", "fnos_api_200003", "trace-403")]
    [InlineData(500, "gateway exploded", "fnos_http_500", null)]
    public async Task ClientTurnsHttpFailuresIntoStageAwareOpenApiErrors(
        int statusCode,
        string body,
        string expectedCode,
        string? expectedRequestId)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await WithSocketAsync(async (listener, socketPath) =>
        {
            using var client = new FnOsOpenApiClient(socketPath, "clipport");
            Task<string> request = ReplyOnceAsync(listener, statusCode, body);

            FnOsOpenApiException error = await Assert.ThrowsAsync<FnOsOpenApiException>(
                () => client.GetSharedAccessibleFoldersAsync(CancellationToken.None));

            Assert.Equal(expectedCode, error.Code);
            Assert.Equal("trim.file.getSharedAccessibleFolders", error.Operation);
            Assert.Equal(statusCode, error.HttpStatusCode);
            Assert.Equal(expectedRequestId, error.RequestId);
            Assert.DoesNotContain("process-token", error.Message, StringComparison.Ordinal);
            await request;
        });
    }

    [Fact]
    public async Task ClientTurnsMalformedJsonIntoAStableOpenApiError()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await WithSocketAsync(async (listener, socketPath) =>
        {
            using var client = new FnOsOpenApiClient(socketPath, "clipport");
            Task<string> request = ReplyOnceAsync(listener, 200, "not-json");

            FnOsOpenApiException error = await Assert.ThrowsAsync<FnOsOpenApiException>(
                () => client.GetSharedAccessibleFoldersAsync(CancellationToken.None));

            Assert.Equal("fnos_invalid_response", error.Code);
            Assert.Equal("trim.file.getSharedAccessibleFolders", error.Operation);
            Assert.Equal(200, error.HttpStatusCode);
            await request;
        });
    }

    [Fact]
    public async Task ClientTurnsBusinessErrorsIntoStageAwareOpenApiErrors()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await WithSocketAsync(async (listener, socketPath) =>
        {
            using var client = new FnOsOpenApiClient(socketPath, "clipport");
            const string body = "{\"reqId\":\"trace-business\",\"code\":200006,\"msg\":\"Denied\",\"data\":{}}";
            Task<string> request = ReplyOnceAsync(listener, 200, body);

            FnOsOpenApiException error = await Assert.ThrowsAsync<FnOsOpenApiException>(
                () => client.GetSharedAccessibleFoldersAsync(CancellationToken.None));

            Assert.Equal("fnos_api_200006", error.Code);
            Assert.Equal("trim.file.getSharedAccessibleFolders", error.Operation);
            Assert.Equal(200, error.HttpStatusCode);
            Assert.Equal("trace-business", error.RequestId);
            await request;
        });
    }

    [Fact]
    public async Task ClientTurnsItsOwnTimeoutIntoAStableOpenApiError()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await WithSocketAsync(async (listener, socketPath) =>
        {
            using var client = new FnOsOpenApiClient(
                socketPath,
                "clipport",
                TimeSpan.FromMilliseconds(50));
            Task server = AcceptAndHoldAsync(listener, TimeSpan.FromMilliseconds(200));

            FnOsOpenApiException error = await Assert.ThrowsAsync<FnOsOpenApiException>(
                () => client.GetSharedAccessibleFoldersAsync(CancellationToken.None));

            Assert.Equal("fnos_timeout", error.Code);
            Assert.Equal("trim.file.getSharedAccessibleFolders", error.Operation);
            await server;
        });
    }

    [Fact]
    public async Task DeleteAuthorizationUsesTheDocumentedOperation()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await WithSocketAsync(async (listener, socketPath) =>
        {
            using var client = new FnOsOpenApiClient(socketPath, "clipport");
            Task<string> request = ReplyOnceAsync(
                listener,
                200,
                "{\"reqId\":\"trace-delete\",\"code\":0,\"msg\":\"\",\"data\":{}}");

            await client.DeleteSharedAccessibleFolderAsync("/vol1/archive", CancellationToken.None);

            string rawRequest = await request;
            Assert.Contains("trim.file.delSharedAccessibleFolder", rawRequest, StringComparison.Ordinal);
            Assert.Contains("/vol1/archive", rawRequest, StringComparison.Ordinal);
        });
    }

    private static async Task<string> ReplyOnceAsync(Socket listener, string path)
    {
        using Socket connection = await listener.AcceptAsync();
        byte[] buffer = new byte[8192];
        int received = await connection.ReceiveAsync(buffer);
        string request = Encoding.UTF8.GetString(buffer, 0, received);
        string body = $"{{\"reqId\":\"1\",\"code\":0,\"msg\":\"\",\"data\":{{\"paths\":[\"{path}\"]}}}}";
        byte[] response = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
            "Connection: close\r\n\r\n" + body);
        await connection.SendAsync(response);
        connection.Shutdown(SocketShutdown.Both);
        return request;
    }

    private static async Task<string> ReplyOnceAsync(Socket listener, int statusCode, string body)
    {
        using Socket connection = await listener.AcceptAsync();
        byte[] buffer = new byte[8192];
        int received = await connection.ReceiveAsync(buffer);
        string request = Encoding.UTF8.GetString(buffer, 0, received);
        string reason = statusCode switch
        {
            200 => "OK",
            403 => "Forbidden",
            500 => "Internal Server Error",
            _ => "Error",
        };
        byte[] response = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
            "Connection: close\r\n\r\n" + body);
        await connection.SendAsync(response);
        connection.Shutdown(SocketShutdown.Both);
        return request;
    }

    private static async Task WithSocketAsync(
        Func<Socket, string, Task> test)
    {
        string socketPath = Path.Combine(
            Path.GetTempPath(),
            $"cp-{Guid.NewGuid():N}"[..20] + ".sock");
        string? originalToken = Environment.GetEnvironmentVariable("TRIM_API_TOKEN");
        using var listener = new Socket(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);
            Environment.SetEnvironmentVariable("TRIM_API_TOKEN", "process-token");
            await test(listener, socketPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRIM_API_TOKEN", originalToken);
            if (File.Exists(socketPath))
            {
                File.Delete(socketPath);
            }
        }
    }

    private static async Task AcceptAndHoldAsync(Socket listener, TimeSpan duration)
    {
        using Socket connection = await listener.AcceptAsync();
        byte[] buffer = new byte[8192];
        await connection.ReceiveAsync(buffer);
        await Task.Delay(duration);
    }
}
