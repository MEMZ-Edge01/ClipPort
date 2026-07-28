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

public sealed class AppSettings
{
    public AppThemeMode Theme { get; set; } = AppThemeMode.System;
    public AppAccentMode Accent { get; set; } = AppAccentMode.System;
    public AppLanguage Language { get; set; } = AppLanguage.SimplifiedChinese;
    public string LogAndReportDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EZ DIT");
}
