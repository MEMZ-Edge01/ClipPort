using ClipPort.FnOS.Contracts;
using ClipPort.FnOS.FnOs;
using ClipPort.FnOS.Realtime;
using ClipPort.FnOS.Security;
using ClipPort.FnOS.Tasks;
using ClipPort.FnOS.Settings;
using ClipPort.Services;
using ClipPort.Models;
using Microsoft.AspNetCore.Mvc;
using ClipPort.FnOS.Updates;

namespace ClipPort.FnOS.Api;

public static class EndpointMappings
{
    private const string MinimumSystemVersion = "1.2.0401";

    public static IEndpointRouteBuilder MapClipPortApi(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder api = endpoints.MapGroup("/api/v1");

        api.MapGet("/session", GetSessionAsync);
        api.MapGet("/authorized-folders", GetAuthorizedFoldersAsync);
        api.MapDelete("/authorized-folders", RevokeAuthorizedFolderAsync);
        api.MapPost("/authorized-folders/validate", ValidateAuthorizedFolderAsync);
        api.MapGet("/tasks", (FnOsTaskManager tasks) => Results.Ok(tasks.Snapshot()));
        api.MapGet("/settings", GetSettingsAsync);
        api.MapPut("/settings", SaveSettingsAsync);
        api.MapPost("/settings/notifications/test", TestNotificationAsync);
        api.MapGet("/update", CheckUpdateAsync);
        api.MapGet("/tasks/{id}", (string id, FnOsTaskManager tasks) => Results.Ok(tasks.Get(id)));
        api.MapPost("/tasks", CreateTaskAsync);
        api.MapPost("/tasks/{id}/pause", PauseTaskAsync);
        api.MapPost("/tasks/{id}/resume", ResumeTaskAsync);
        api.MapPost("/tasks/{id}/cancel", CancelTaskAsync);
        api.MapPost("/tasks/{id}/restart", RestartTaskAsync);
        api.MapPost("/tasks/{id}/verify", VerifyAgainAsync);
        api.MapPost("/tasks/{id}/duplicates", SubmitDuplicateDecisionsAsync);
        api.MapPost("/tasks/{id}/failures", SubmitFailureActionAsync);
        api.MapDelete("/tasks/{id}", DeleteTaskAsync);
        api.MapPost("/tasks/batch-delete", DeleteTasksAsync);
        api.MapPost("/reports/export", ExportReportsAsync);
        api.MapGet("/tasks/{id}/report", DownloadReport);

        endpoints.Map("/ws", AcceptWebSocketAsync);
        return endpoints;
    }

    private static IResult GetSessionAsync(
        HttpContext context,
        CsrfTokenStore csrfTokens)
    {
        GatewayUser user = GatewayUser.From(context);
        string language = NormalizeLanguage(
            Environment.GetEnvironmentVariable("TRIM_SYS_LANGUAGE") ?? "zh-CN");
        string systemVersion =
            Environment.GetEnvironmentVariable("TRIM_SYS_VERSION") ?? MinimumSystemVersion;
        return Results.Ok(new SessionResponse(
            user.IsAdmin,
            user.UserId,
            user.Username,
            csrfTokens.GetOrCreate(user.UserId),
            language,
            systemVersion,
            IsCompatible(systemVersion)));
    }

    private static async Task<IResult> GetAuthorizedFoldersAsync(
        HttpContext context,
        AuthorizedFolderModule folders,
        CancellationToken cancellationToken)
    {
        GatewayUser user = GatewayUser.From(context);
        string language = Environment.GetEnvironmentVariable("TRIM_SYS_LANGUAGE") ?? "zh-CN";
        return Results.Ok(await folders.GetFoldersAsync(
            user.UserId,
            NormalizeLanguage(language),
            cancellationToken));
    }

    private static async Task<IResult> RevokeAuthorizedFolderAsync(
        [FromBody] RevokeAuthorizedFolderRequest request,
        AuthorizedFolderModule folders,
        FnOsTaskManager tasks,
        CancellationToken cancellationToken)
    {
        tasks.EnsureNoActiveTaskUses(request.Path);
        await folders.RevokeAsync(request.Path, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ValidateAuthorizedFolderAsync(
        HttpContext context,
        ValidateAuthorizedFolderRequest request,
        AuthorizedFolderModule folders,
        CancellationToken cancellationToken)
    {
        await folders.ValidateDirectoryAsync(
            GatewayUser.From(context).UserId,
            request.Path,
            request.RequireWrite,
            cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateTaskAsync(
        HttpContext context,
        CreateTaskRequest request,
        FnOsTaskManager tasks,
        CancellationToken cancellationToken)
    {
        FnOsTaskRecord record = await tasks.CreateAsync(
            GatewayUser.From(context).UserId,
            request,
            cancellationToken);
        return Results.Created($"/api/v1/tasks/{record.Id}", record);
    }

    private static async Task<IResult> GetSettingsAsync(
        FnOsSettingsStore settings,
        CancellationToken cancellationToken) =>
        Results.Ok(await settings.GetResponseAsync(cancellationToken));

    private static async Task<IResult> SaveSettingsAsync(
        SaveFnOsSettingsRequest request,
        FnOsSettingsStore settings,
        CancellationToken cancellationToken) =>
        Results.Ok(await settings.SaveAsync(request, cancellationToken));

    private static async Task<IResult> TestNotificationAsync(
        NotificationTestRequest request,
        FnOsSettingsStore settings,
        NotificationService notifications,
        CancellationToken cancellationToken)
    {
        NotificationChannelSettings channel = await settings.MaterializeTestChannelAsync(
            request.Channel,
            cancellationToken);
        FnOsSettingsDocument current = await settings.LoadAsync(cancellationToken);
        ResourceService.SetLanguage(current.Language);
        return Results.Ok(await notifications.SendTestAsync(channel, cancellationToken));
    }

    private static async Task<IResult> CheckUpdateAsync(
        FnOsUpdateService updates,
        CancellationToken cancellationToken) =>
        Results.Ok(await updates.CheckAsync(cancellationToken));

    private static async Task<IResult> PauseTaskAsync(
        string id,
        FnOsTaskManager tasks,
        CancellationToken cancellationToken)
    {
        await tasks.PauseAsync(id, cancellationToken);
        return Results.Ok(tasks.Get(id));
    }

    private static async Task<IResult> ResumeTaskAsync(
        string id,
        FnOsTaskManager tasks,
        CancellationToken cancellationToken)
    {
        await tasks.ResumeAsync(id, cancellationToken);
        return Results.Ok(tasks.Get(id));
    }

    private static async Task<IResult> CancelTaskAsync(
        string id,
        FnOsTaskManager tasks,
        CancellationToken cancellationToken)
    {
        await tasks.CancelAsync(id);
        return Results.Accepted($"/api/v1/tasks/{id}");
    }

    private static async Task<IResult> RestartTaskAsync(
        HttpContext context,
        string id,
        FnOsTaskManager tasks,
        CancellationToken cancellationToken) =>
        Results.Created(
            "/api/v1/tasks",
            await tasks.RestartAsync(
                GatewayUser.From(context).UserId,
                id,
                cancellationToken));

    private static async Task<IResult> VerifyAgainAsync(
        HttpContext context,
        string id,
        FnOsTaskManager tasks,
        CancellationToken cancellationToken) =>
        Results.Created(
            "/api/v1/tasks",
            await tasks.VerifyAgainAsync(
                GatewayUser.From(context).UserId,
                id,
                cancellationToken));

    private static async Task<IResult> SubmitDuplicateDecisionsAsync(
        string id,
        DuplicateDecisionRequest request,
        FnOsTaskManager tasks)
    {
        await tasks.SubmitDuplicateDecisionsAsync(id, request);
        return Results.Accepted($"/api/v1/tasks/{id}");
    }

    private static async Task<IResult> SubmitFailureActionAsync(
        string id,
        FailureActionRequest request,
        FnOsTaskManager tasks)
    {
        await tasks.SubmitFailureActionAsync(id, request);
        return Results.Accepted($"/api/v1/tasks/{id}");
    }

    private static async Task<IResult> DeleteTaskAsync(
        string id,
        FnOsTaskManager tasks,
        CancellationToken cancellationToken)
    {
        await tasks.DeleteAsync(id, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteTasksAsync(
        BatchTaskRequest request,
        FnOsTaskManager tasks,
        CancellationToken cancellationToken)
    {
        await tasks.DeleteManyAsync(request.TaskIds, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ExportReportsAsync(
        HttpContext context,
        BatchReportExportRequest request,
        AuthorizedFolderModule folders,
        FnOsTaskManager tasks,
        CancellationToken cancellationToken)
    {
        string destination = await folders.ValidateDirectoryAsync(
            GatewayUser.From(context).UserId,
            request.DestinationDirectory,
            requireWrite: true,
            cancellationToken);
        return Results.Ok(await tasks.ExportReportsAsync(
            request.TaskIds,
            destination,
            cancellationToken));
    }

    private static IResult DownloadReport(string id, FnOsTaskManager tasks)
    {
        string path = tasks.GetReportPath(id);
        if (!File.Exists(path))
        {
            throw new TaskManagerException("report_not_found", "The task report is not available.");
        }
        return Results.File(path, "text/plain; charset=utf-8", $"clipport-{id}.txt");
    }

    private static Task AcceptWebSocketAsync(
        HttpContext context,
        TaskEventHub events,
        FnOsTaskManager tasks) =>
        events.AcceptAsync(context, () => tasks.Snapshot(), context.RequestAborted);

    internal static bool IsCompatible(string version)
    {
        int[] actual = ParseVersion(version);
        int[] minimum = ParseVersion(MinimumSystemVersion);
        for (int index = 0; index < Math.Max(actual.Length, minimum.Length); index++)
        {
            int left = index < actual.Length ? actual[index] : 0;
            int right = index < minimum.Length ? minimum[index] : 0;
            if (left != right)
            {
                return left > right;
            }
        }
        return true;
    }

    private static int[] ParseVersion(string value) =>
        value.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out int result) ? result : 0)
            .ToArray();

    private static string NormalizeLanguage(string language) =>
        language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";
}
