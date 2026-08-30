using ClipPort.FnOS.Contracts;
using ClipPort.FnOS.FnOs;
using ClipPort.FnOS.Tasks;

namespace ClipPort.FnOS.Infrastructure;

public sealed class ApiExceptionMiddleware(
    RequestDelegate next,
    ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AccessValidationException ex)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest,
                new ErrorResponse(ex.Code, ex.Message, ex.Details));
        }
        catch (TaskManagerException ex)
        {
            int status = ex.Code switch
            {
                "task_not_found" or "report_not_found" => StatusCodes.Status404NotFound,
                "invalid_request" => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status409Conflict,
            };
            await WriteAsync(context, status,
                new ErrorResponse(ex.Code, ex.Message, ex.Details));
        }
        catch (FnOsOpenApiException ex)
        {
            logger.LogWarning(
                "fnOS system API call failed during {Operation} with code {Code}, HTTP status {StatusCode}, request id {RequestId}.",
                ex.Operation,
                ex.Code,
                ex.HttpStatusCode,
                ex.RequestId);
            await WriteAsync(context, StatusCodes.Status502BadGateway,
                new ErrorResponse(
                    ex.Code,
                    "fnOS 系统接口未能完成目录授权操作。",
                    new
                    {
                        operation = ex.Operation,
                        statusCode = ex.HttpStatusCode,
                        requestId = ex.RequestId,
                    }));
        }
        catch (BadHttpRequestException ex)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest,
                new ErrorResponse("invalid_request", ex.Message));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "A required upstream HTTP service could not complete the request.");
            await WriteAsync(context, StatusCodes.Status502BadGateway,
                new ErrorResponse("upstream_unavailable", "上游服务暂时不可用。"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled fnOS API request failure.");
            await WriteAsync(context, StatusCodes.Status500InternalServerError,
                new ErrorResponse("internal_error", "The request could not be completed."));
        }
    }

    private static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        ErrorResponse response)
    {
        if (context.Response.HasStarted)
        {
            return;
        }
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(response);
    }
}
