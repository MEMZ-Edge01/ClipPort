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
    public string LogAndReportDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ClipPort");
}
