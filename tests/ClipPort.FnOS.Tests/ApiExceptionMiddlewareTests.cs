using System.Text.Json;
using ClipPort.FnOS.Contracts;
using ClipPort.FnOS.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClipPort.FnOS.Tests;

public sealed class ApiExceptionMiddlewareTests
{
    [Fact]
    public async Task UnexpectedFailuresReturnStableLocalizedBodyWithoutExceptionDetails()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new ApiExceptionMiddleware(
            _ => throw new InvalidOperationException("sensitive diagnostic detail"),
            NullLogger<ApiExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        ErrorResponse? response = await JsonSerializer.DeserializeAsync<ErrorResponse>(
            context.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        string responseText = await ReadResponseAsync(context.Response.Body);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("internal_error", response?.Code);
        Assert.Contains("fnOS 应用日志", response?.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive diagnostic detail", responseText, StringComparison.Ordinal);
        Assert.DoesNotContain("The request could not be completed", responseText, StringComparison.Ordinal);
    }

    private static async Task<string> ReadResponseAsync(Stream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
