namespace EZDIT.Models;

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
    English
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
        new(AppLanguage.English, "en-US", "Settings.English")
    ];

    public static AppLanguageDefinition Get(AppLanguage language) =>
        Supported.First(definition => definition.Language == language);
}

public sealed class AppSettings
{
    public AppThemeMode Theme { get; set; } = AppThemeMode.System;
    public AppAccentMode Accent { get; set; } = AppAccentMode.System;
    public AppLanguage Language { get; set; } = AppLanguage.SimplifiedChinese;
    public string LogAndReportDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EZ DIT");
}
