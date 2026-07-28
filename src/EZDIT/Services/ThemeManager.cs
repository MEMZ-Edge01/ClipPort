using EZDIT.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace EZDIT.Services;

public static class ThemeManager
{
    private static readonly UISettings UiSettings = new();

    public static void Apply(FrameworkElement root, AppSettings settings)
    {
        root.RequestedTheme = settings.Theme switch
        {
            AppThemeMode.Light => ElementTheme.Light,
            AppThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        bool dark = settings.Theme == AppThemeMode.Dark ||
                    settings.Theme == AppThemeMode.System && IsSystemDark();
        ApplyPalette(dark);
        ApplyAccent(settings.Accent, dark);
    }

    private static bool IsSystemDark()
    {
        Color background = UiSettings.GetColorValue(UIColorType.Background);
        return (background.R + background.G + background.B) / 3 < 128;
    }

    private static void ApplyPalette(bool dark)
    {
        if (dark)
        {
            SetBrush("AppBackgroundBrush", "#171717");
            SetBrush("SidebarBrush", "#1C1C1C");
            SetBrush("PanelBrush", "#242424");
            SetBrush("PanelRaisedBrush", "#333333");
            SetBrush("CardBrush", "#2C2C2C");
            SetBrush("CardOverlayBrush", "#2B2B2B");
            SetBrush("TitleBarBrush", "#171717");
            SetBrush("LogBarBrush", "#1C1C1C");
            SetBrush("DialogBackgroundBrush", "#282828");
            SetBrush("ControlSecondaryBrush", "#303030");
            SetBrush("ControlSecondaryHoverBrush", "#3A3A3A");
            SetBrush("DefaultTextBrush", "#F5F5F5");
            SetBrush("SecondaryTextBrush", "#D4D4D4");
            SetBrush("MutedTextBrush", "#A3A3A3");
            SetBrush("DisabledTextBrush", "#737373");
            SetBrush("IconDefaultBrush", "#F5F5F5");
            SetBrush("IconSecondaryBrush", "#D4D4D4");
            SetBrush("IconTertiaryBrush", "#A3A3A3");
            SetBrush("BorderNeutralL1Brush", "#33FFFFFF");
            SetBrush("BorderNeutralL2Brush", "#52FFFFFF");
            SetBrush("OverlayL1Brush", "#14FFFFFF");
            SetBrush("OverlayL2Brush", "#1FFFFFFF");
            SetBrush("OverlayL3Brush", "#29FFFFFF");
            SetBrush("OverlayL4Brush", "#33FFFFFF");
            SetBrush("ProgressTrackBrush", "#33FFFFFF");
        }
        else
        {
            SetBrush("AppBackgroundBrush", "#FFFFFF");
            SetBrush("SidebarBrush", "#EDEDED");
            SetBrush("PanelBrush", "#F5F5F5");
            SetBrush("PanelRaisedBrush", "#13737373");
            SetBrush("CardBrush", "#FFFFFF");
            SetBrush("CardOverlayBrush", "#13737373");
            SetBrush("TitleBarBrush", "#FFFFFF");
            SetBrush("LogBarBrush", "#EDEDED");
            SetBrush("DialogBackgroundBrush", "#FFFFFF");
            SetBrush("ControlSecondaryBrush", "#13737373");
            SetBrush("ControlSecondaryHoverBrush", "#1F737373");
            SetBrush("DefaultTextBrush", "#171717");
            SetBrush("SecondaryTextBrush", "#404040");
            SetBrush("MutedTextBrush", "#737373");
            SetBrush("DisabledTextBrush", "#A1A1A1");
            SetBrush("IconDefaultBrush", "#262626");
            SetBrush("IconSecondaryBrush", "#404040");
            SetBrush("IconTertiaryBrush", "#737373");
            SetBrush("BorderNeutralL1Brush", "#1F737373");
            SetBrush("BorderNeutralL2Brush", "#2E737373");
            SetBrush("OverlayL1Brush", "#13737373");
            SetBrush("OverlayL2Brush", "#1F737373");
            SetBrush("OverlayL3Brush", "#29737373");
            SetBrush("OverlayL4Brush", "#33737373");
            SetBrush("ProgressTrackBrush", "#1F737373");
        }
    }

    private static void ApplyAccent(AppAccentMode accentMode, bool dark)
    {
        RefreshSystemAccentPreview();

        Color accent = accentMode switch
        {
            AppAccentMode.Seafoam => FromHex("#00B7C3"),
            AppAccentMode.BrightRose => FromHex("#EA005E"),
            AppAccentMode.Gold => FromHex("#FFB900"),
            AppAccentMode.Mint => FromHex("#00B294"),
            AppAccentMode.PurpleShadow => FromHex("#8E8CD8"),
            _ => UiSettings.GetColorValue(UIColorType.Accent)
        };

        Color hover = Mix(accent, Colors.White, 0.16);
        Color active = Mix(accent, Colors.Black, 0.18);
        Color pale = Mix(accent, dark ? Colors.Black : Colors.White, dark ? 0.55 : 0.78);
        Color selectionBg = Mix(accent, dark ? Colors.Black : Colors.White, dark ? 0.40 : 0.65);
        SetBrush("AccentBrush", accent);
        SetBrush("AccentHoverBrush", hover);
        SetBrush("AccentActiveBrush", active);
        SetBrush("AccentDisabledBrush", Mix(accent, dark ? Colors.Black : Colors.White, dark ? 0.35 : 0.50));
        SetBrush("AccentSoftBrush", pale);
        SetBrush("AccentSelectionPaleBrush", selectionBg);
        SetBrush("AccentSelectionPalePointerOverBrush", Mix(selectionBg, accent, 0.12));
        SetBrush("AccentSelectionPalePressedBrush", Mix(selectionBg, accent, 0.22));
        SetBrush("AccentTextBrush", dark ? Mix(accent, Colors.White, 0.32) : active);
        SetBrush("BorderBrandBrush", accent);
        SetBrush("Brand300Brush", hover);
        SetBrush("Brand200Brush", pale);
        SetBrush("AccentButtonTextBrush", RelativeLuminance(accent) > 0.58 ? Colors.Black : Colors.White);
        SetBrush("ListViewItemCheckBoxSelectedBrush", accent);
        SetBrush("ListViewItemCheckBoxSelectedPointerOverBrush", hover);
        SetBrush("ListViewItemCheckBoxSelectedPressedBrush", active);
        SetBrush("ListViewItemCheckBoxPointerOverBorderBrush", accent);
        SetBrush("ListViewItemCheckBoxPressedBorderBrush", active);
        SetBrush("ListViewItemCheckHintThemeBrush", accent);
        SetBrush("ListViewItemCheckSelectingThemeBrush", accent);
        SetBrush("ListViewItemBackgroundSelected", selectionBg);
        SetBrush("ListViewItemBackgroundSelectedPointerOver", Mix(selectionBg, accent, 0.12));
        SetBrush("ListViewItemBackgroundSelectedPressed", Mix(selectionBg, accent, 0.22));
        SetBrush("ListViewItemForegroundSelected", dark ? hover : active);
    }

    /// <summary>
    /// Refreshes the settings preview without changing the app's selected accent.
    /// </summary>
    public static void RefreshSystemAccentPreview() =>
        SetBrush("WindowsAccentPreviewBrush", UiSettings.GetColorValue(UIColorType.Accent));

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            double channel = value / 255d;
            return channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static Color Mix(Color left, Color right, double amount) => Color.FromArgb(
        255,
        (byte)(left.R + (right.R - left.R) * amount),
        (byte)(left.G + (right.G - left.G) * amount),
        (byte)(left.B + (right.B - left.B) * amount));

    private static Color FromHex(string value)
    {
        string hex = value.TrimStart('#');
        if (hex.Length == 8)
        {
            return Color.FromArgb(
                Convert.ToByte(hex[0..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16),
                Convert.ToByte(hex[6..8], 16));
        }
        return Color.FromArgb(
            255,
            Convert.ToByte(hex[0..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
    }

    private static void SetBrush(string key, string hex) => SetBrush(key, FromHex(hex));

    private static void SetBrush(string key, Color color)
    {
        if (FindBrush(Application.Current.Resources, key) is SolidColorBrush brush)
        {
            brush.Color = color;
        }
    }

    private static SolidColorBrush? FindBrush(ResourceDictionary resources, string key)
    {
        if (resources.ContainsKey(key))
        {
            return resources[key] as SolidColorBrush;
        }
        foreach (ResourceDictionary merged in resources.MergedDictionaries)
        {
            SolidColorBrush? brush = FindBrush(merged, key);
            if (brush is not null)
            {
                return brush;
            }
        }
        return null;
    }
}
