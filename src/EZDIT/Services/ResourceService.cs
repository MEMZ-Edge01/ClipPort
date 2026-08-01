using System.Xml.Linq;
using EZDIT.Models;

namespace EZDIT.Services;

/// <summary>
/// Provides localized strings by loading .resw resource files directly.
/// Resource keys follow a structured convention:
///   XAML x:Uid:  ElementUid.PropertyName
///   Code-behind: Category.Description
/// </summary>
public static class ResourceService
{
    private static readonly IReadOnlyDictionary<AppLanguage, Dictionary<string, string>> _resources;
    private static readonly Dictionary<string, string> _defaultValueToKey = new();

    private static AppLanguage _currentLanguage = AppLanguage.SimplifiedChinese;

    static ResourceService()
    {
        _resources = AppLanguages.Supported.ToDictionary(
            definition => definition.Language,
            definition => LoadResw(Path.Combine(
                "Strings",
                definition.LanguageTag,
                "Resources.resw")));

        Dictionary<string, string> defaultResources = _resources[AppLanguage.SimplifiedChinese];
        if (defaultResources.Count == 0)
        {
            throw new InvalidOperationException(
                "The default localization resources could not be loaded.");
        }
        foreach (var (key, defaultValue) in defaultResources)
        {
            if (_defaultValueToKey.TryGetValue(defaultValue, out string? previousKey) &&
                previousKey != key)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Resource collision: default text \"{defaultValue}\" maps to " +
                    $"both \"{previousKey}\" and \"{key}\".");
                continue;
            }
            _defaultValueToKey[defaultValue] = key;
        }
    }

    public static void SetLanguage(AppLanguage language)
    {
        _currentLanguage = language;
    }

    public static AppLanguage GetLanguage() => _currentLanguage;

    /// <summary>
    /// Returns the localized string for <paramref name="key"/>.
    /// Falls back to the key itself when the resource is missing.
    /// When the key is not found and contains persisted Simplified Chinese
    /// text, attempts a reverse lookup and returns the matching value from
    /// the currently selected language.
    /// </summary>
    public static string GetString(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return key ?? string.Empty;
        }

        Dictionary<string, string> selectedResources = _resources[_currentLanguage];
        if (selectedResources.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value))
        {
            return value;
        }

        // Compatibility fallback for persisted messages created by older builds,
        // which stored the Simplified Chinese value instead of the resource key.
        if (_defaultValueToKey.TryGetValue(key, out string? legacyResourceKey) &&
            selectedResources.TryGetValue(legacyResourceKey, out string? translatedLegacyValue))
        {
            return translatedLegacyValue;
        }

        Dictionary<string, string> defaultResources = _resources[AppLanguage.SimplifiedChinese];
        if (defaultResources.TryGetValue(key, out string? defaultValue) &&
            !string.IsNullOrEmpty(defaultValue))
        {
            return defaultValue;
        }

        System.Diagnostics.Debug.WriteLine($"Missing localized resource: {key}");
        return key;
    }

    /// <summary>
    /// Convenience overload that formats a localized string.
    /// </summary>
    public static string Format(string key, params object?[] args) =>
        string.Format(GetString(key), args);

    private static Dictionary<string, string> LoadResw(string relativePath)
    {
        var dict = new Dictionary<string, string>();
        try
        {
            string fullPath = Path.Combine(AppContext.BaseDirectory, relativePath);
            if (!File.Exists(fullPath))
            {
                return dict;
            }

            var doc = XDocument.Load(fullPath);
            foreach (var data in doc.Descendants("data"))
            {
                string? name = data.Attribute("name")?.Value;
                string? value = data.Element("value")?.Value;
                if (!string.IsNullOrEmpty(name) && value is not null)
                {
                    dict[name] = value;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError(
                $"Failed to load localization resource '{relativePath}': {ex}");
        }

        return dict;
    }

}
