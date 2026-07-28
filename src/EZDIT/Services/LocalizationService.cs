using System.Xml.Linq;
using EZDIT.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EZDIT.Services;

public static partial class LocalizationService
{
    private static readonly Dictionary<string, string>? _zhResources;
    private static readonly Dictionary<string, string>? _enResources;

    public static AppLanguage CurrentLanguage { get; set; } = AppLanguage.SimplifiedChinese;

    static LocalizationService()
    {
        _zhResources = LoadReswFile(Path.Combine("Strings", "zh-CN", "Resources.resw"));
        _enResources = LoadReswFile(Path.Combine("Strings", "en-US", "Resources.resw"));
    }

    private static Dictionary<string, string> LoadReswFile(string relativePath)
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
            // Resource file not found or parse error — return empty dict.
        }

        return dict;
    }

    /// <summary>
    /// Changes the active language for all subsequent <see cref="Text"/> calls.
    /// No restart or PrimaryLanguageOverride is needed — resources are loaded from
    /// the .resw files directly.
    /// </summary>
    public static void SetLanguage(AppLanguage language)
    {
        CurrentLanguage = language;
    }

    public static string Text(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var dict = CurrentLanguage == AppLanguage.English ? _enResources : _zhResources;
        if (dict is not null && dict.TryGetValue(value, out string? result) && !string.IsNullOrEmpty(result))
        {
            return result;
        }

        return value;
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(Text(key), args);

    public static void Apply(DependencyObject root)
    {
        if (ToolTipService.GetToolTip(root) is string toolTip)
        {
            ToolTipService.SetToolTip(root, Text(toolTip));
        }
        switch (root)
        {
            case TextBlock textBlock:
                textBlock.Text = Text(textBlock.Text);
                break;
            case ToggleSwitch toggle:
                toggle.Header = TranslateObject(toggle.Header);
                toggle.OnContent = TranslateObject(toggle.OnContent);
                toggle.OffContent = TranslateObject(toggle.OffContent);
                break;
            case TextBox textBox:
                textBox.Header = TranslateObject(textBox.Header);
                textBox.PlaceholderText = Text(textBox.PlaceholderText);
                break;
            case ComboBox comboBox:
                comboBox.Header = TranslateObject(comboBox.Header);
                break;
            case ComboBoxItem:
                // ComboBoxItem.Content is translated via Tag in SettingsView.Localize(),
                // not here, because VisualTreeHelper may not reach items inside a
                // collapsed UserControl. Skipping prevents double-translation.
                break;
            case ContentControl contentControl when contentControl.Content is string content:
                contentControl.Content = Text(content);
                break;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            Apply(VisualTreeHelper.GetChild(root, index));
        }
    }

    public static void Apply(ContentDialog dialog)
    {
        dialog.Title = TranslateObject(dialog.Title);
        dialog.PrimaryButtonText = Text(dialog.PrimaryButtonText);
        dialog.SecondaryButtonText = Text(dialog.SecondaryButtonText);
        dialog.CloseButtonText = Text(dialog.CloseButtonText);
        if (dialog.Content is string content)
        {
            dialog.Content = Text(content);
        }
        else if (dialog.Content is DependencyObject root)
        {
            Apply(root);
        }
    }

    private static object? TranslateObject(object? value) =>
        value is string text ? Text(text) : value;
}
