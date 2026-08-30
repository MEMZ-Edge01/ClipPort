using ClipPort.Models;

namespace ClipPort.FnOS.Settings;

public sealed class FnOsSettingsDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public AppThemeMode Theme { get; set; } = AppThemeMode.System;
    public AppAccentMode Accent { get; set; } = AppAccentMode.System;
    public AppLanguage Language { get; set; } = AppLanguage.SimplifiedChinese;
    public string? ReportExportDirectory { get; set; }
    public NotificationSettings Notifications { get; set; } = new();
}

public sealed record FnOsNotificationChannelView(
    string Id,
    string DisplayName,
    NotificationChannelKind Kind,
    bool IsEnabled,
    bool HasEndpoint,
    string SmtpHost,
    int SmtpPort,
    string SmtpUsername,
    bool HasSmtpPassword,
    string SmtpFrom,
    string SmtpRecipients);

public sealed record FnOsSettingsResponse(
    int Version,
    AppThemeMode Theme,
    AppAccentMode Accent,
    AppLanguage Language,
    string? ReportExportDirectory,
    bool NotifyOnTaskCompleted,
    bool NotifyOnTaskFailed,
    IReadOnlyList<FnOsNotificationChannelView> Channels);

public sealed record FnOsNotificationChannelUpdate(
    string? Id,
    string DisplayName,
    NotificationChannelKind Kind,
    bool IsEnabled,
    string? Endpoint,
    bool ClearEndpoint,
    string SmtpHost,
    int SmtpPort,
    string SmtpUsername,
    string? SmtpPassword,
    bool ClearSmtpPassword,
    string SmtpFrom,
    string SmtpRecipients);

public sealed record SaveFnOsSettingsRequest(
    AppThemeMode Theme,
    AppAccentMode Accent,
    AppLanguage Language,
    string? ReportExportDirectory,
    bool NotifyOnTaskCompleted,
    bool NotifyOnTaskFailed,
    IReadOnlyList<FnOsNotificationChannelUpdate> Channels);

public sealed record NotificationTestRequest(FnOsNotificationChannelUpdate Channel);
