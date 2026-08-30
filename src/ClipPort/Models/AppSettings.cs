namespace ClipPort.Models;

public enum AppThemeMode
{
    System,
    Light,
    Dark
}

public enum AppAccentMode
{
    System,
    Seafoam,
    BrightRose,
    Gold,
    Mint,
    PurpleShadow
}

public enum AppLanguage
{
    SimplifiedChinese,
    English,
    ClassicalChinese
}

public enum NotificationChannelKind
{
    WeCom,
    DingTalk,
    Feishu,
    Bark,
    Smtp
}

public sealed record AppLanguageDefinition(
    AppLanguage Language,
    string LanguageTag,
    string DisplayNameResourceKey);

/// <summary>
/// Central registry for languages supported by the application.
/// Adding a language only requires a new enum value, one registry entry,
/// and a matching Strings/{language-tag}/Resources.resw file.
/// </summary>
public static class AppLanguages
{
    public static IReadOnlyList<AppLanguageDefinition> Supported { get; } =
    [
        new(AppLanguage.SimplifiedChinese, "zh-CN", "Settings.SimplifiedChinese"),
        new(AppLanguage.English, "en-US", "Settings.English"),
        new(AppLanguage.ClassicalChinese, "lzh", "Settings.ClassicalChinese")
    ];

    public static AppLanguageDefinition Get(AppLanguage language) =>
        Supported.FirstOrDefault(definition => definition.Language == language) ??
        Supported[0];
}

public sealed class AppSettings
{
    public AppThemeMode Theme { get; set; } = AppThemeMode.System;
    public AppAccentMode Accent { get; set; } = AppAccentMode.System;
    public AppLanguage Language { get; set; } = AppLanguage.SimplifiedChinese;
    public bool ExplorerContextMenuEnabled { get; set; }
    public bool LegacyExplorerContextMenuEnabled { get; set; }
    public NotificationSettings Notifications { get; set; } = new();
    public string LogAndReportDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ClipPort");
}

public sealed class NotificationSettings
{
    public bool NotifyOnTaskCompleted { get; set; } = true;
    public bool NotifyOnTaskFailed { get; set; } = true;
    public List<NotificationChannelSettings> Channels { get; set; } = [];
}

public sealed class NotificationChannelSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public NotificationChannelKind Kind { get; set; } = NotificationChannelKind.Feishu;
    public bool IsEnabled { get; set; } = true;

    // Webhook providers and Bark use this HTTP(S) endpoint. WebSocket URLs are
    // deliberately rejected because these providers expose HTTP push APIs.
    public string Endpoint { get; set; } = string.Empty;

    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 465;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string SmtpFrom { get; set; } = string.Empty;
    public string SmtpRecipients { get; set; } = string.Empty;

    /// <summary>
    /// Creates a stable snapshot for persistence and background delivery.
    /// </summary>
    public NotificationChannelSettings Clone() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Kind = Kind,
        IsEnabled = IsEnabled,
        Endpoint = Endpoint,
        SmtpHost = SmtpHost,
        SmtpPort = SmtpPort,
        SmtpUsername = SmtpUsername,
        SmtpPassword = SmtpPassword,
        SmtpFrom = SmtpFrom,
        SmtpRecipients = SmtpRecipients
    };
}
