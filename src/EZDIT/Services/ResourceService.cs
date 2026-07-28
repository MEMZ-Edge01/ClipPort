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
    private static readonly Dictionary<string, string> _zhResources;
    private static readonly Dictionary<string, string> _enResources;
    private static readonly Dictionary<string, string> _zhValueToEn = new();

    private static AppLanguage _currentLanguage = AppLanguage.SimplifiedChinese;

    static ResourceService()
    {
        _zhResources = LoadResw(Path.Combine("Strings", "zh-CN", "Resources.resw"));
        _enResources = LoadResw(Path.Combine("Strings", "en-US", "Resources.resw"));

        // Build reverse mapping: zh-CN value → en-US value.
        // This allows ApplyResources to translate Chinese XAML text
        // (which isn't a structured key) by looking up the matching
        // structured key's en-US translation.
        foreach (var (key, zhValue) in _zhResources)
        {
            if (_enResources.TryGetValue(key, out string? enValue) && !string.IsNullOrEmpty(enValue))
            {
                if (_zhValueToEn.TryGetValue(zhValue, out string? previous) && previous != enValue)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Resource collision: Chinese text \"{zhValue}\" maps to " +
                        $"both \"{previous}\" (existing) and \"{enValue}\" (key \"{key}\").");
                }
                _zhValueToEn[zhValue] = enValue;
            }
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
    /// When the key is not found and contains Chinese text, attempts a
    /// reverse lookup: treats the Chinese text as a zh-CN value and
    /// returns the corresponding en-US value (or the original for zh-CN).
    /// </summary>
    public static string GetString(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return key;
        }

        var dict = _currentLanguage == AppLanguage.English ? _enResources : _zhResources;
        if (dict.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value))
        {
            return value;
        }

        // Fallback: if the key looks like Chinese text used by ApplyResources,
        // try reverse lookup — find the structured key whose zh-CN value matches,
        // then return the translation from the current language dictionary.
        if (ContainsChinese(key))
        {
            if (_zhValueToEn.TryGetValue(key, out string? enFromZh))
            {
                return _currentLanguage == AppLanguage.English ? enFromZh : key;
            }
        }

        return key;
    }

    private static bool ContainsChinese(string text)
    {
        foreach (char c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF)
                return true;
        }
        return false;
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
        catch
        {
            // Missing or malformed resource file — return empty dict.
        }

        return dict;
    }

    /// <summary>
    /// One-time application of localized resources to the visual tree.
    /// Uses the current text/content of each element as the resource key
    /// and replaces it with the localized version. Safe because it runs
    /// exactly once at startup; language changes require a restart.
    /// </summary>
    public static void ApplyResources(Microsoft.UI.Xaml.DependencyObject root)
    {
        if (Microsoft.UI.Xaml.Controls.ToolTipService.GetToolTip(root) is string toolTip)
        {
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(root, GetString(toolTip));
        }

        switch (root)
        {
            case Microsoft.UI.Xaml.Controls.TextBlock textBlock:
                if (!string.IsNullOrEmpty(textBlock.Text))
                    textBlock.Text = GetString(textBlock.Text);
                break;
            case Microsoft.UI.Xaml.Controls.ToggleSwitch toggle:
                if (toggle.Header is string header)
                    toggle.Header = GetString(header);
                if (toggle.OnContent is string onContent)
                    toggle.OnContent = GetString(onContent);
                if (toggle.OffContent is string offContent)
                    toggle.OffContent = GetString(offContent);
                break;
            case Microsoft.UI.Xaml.Controls.TextBox textBox:
                if (textBox.Header is string tbHeader)
                    textBox.Header = GetString(tbHeader);
                if (!string.IsNullOrEmpty(textBox.PlaceholderText))
                    textBox.PlaceholderText = GetString(textBox.PlaceholderText);
                break;
            case Microsoft.UI.Xaml.Controls.ComboBox comboBox:
                if (comboBox.Header is string cbHeader)
                    comboBox.Header = GetString(cbHeader);
                break;
            case Microsoft.UI.Xaml.Controls.ContentControl cc when cc.Content is string content:
                cc.Content = GetString(content);
                break;
            case Microsoft.UI.Xaml.Controls.RadioButton radio when radio.Content is string radioContent:
                radio.Content = GetString(radioContent);
                break;
            case Microsoft.UI.Xaml.Controls.CheckBox check when check.Content is string checkContent:
                check.Content = GetString(checkContent);
                break;
        }

        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            ApplyResources(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
        }
    }

    /// <summary>
    /// Applies localized resources to a ContentDialog (title, buttons, content).
    /// </summary>
    public static void ApplyResources(Microsoft.UI.Xaml.Controls.ContentDialog dialog)
    {
        if (dialog.Title is string title)
            dialog.Title = GetString(title);
        if (!string.IsNullOrEmpty(dialog.PrimaryButtonText))
            dialog.PrimaryButtonText = GetString(dialog.PrimaryButtonText);
        if (!string.IsNullOrEmpty(dialog.SecondaryButtonText))
            dialog.SecondaryButtonText = GetString(dialog.SecondaryButtonText);
        if (!string.IsNullOrEmpty(dialog.CloseButtonText))
            dialog.CloseButtonText = GetString(dialog.CloseButtonText);
        if (dialog.Content is string content)
            dialog.Content = GetString(content);
        else if (dialog.Content is Microsoft.UI.Xaml.DependencyObject dObj)
            ApplyResources(dObj);
    }
}
