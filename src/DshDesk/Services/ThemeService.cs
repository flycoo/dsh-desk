using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DshDesk.Services;

public static class ThemeService
{
    public static void ApplyCurrentTheme(ResourceDictionary resources)
    {
        ApplyTheme(resources, SystemUsesLightTheme());
    }

    public static void ApplyTheme(ResourceDictionary resources, bool isLight)
    {
        Set(resources, "WindowBackground", isLight ? "#FAFBFB" : "#0F1312");
        Set(resources, "ChromeBackground", isLight ? "#F4F7F6" : "#171C1A");
        Set(resources, "PanelBackground", isLight ? "#FFFFFF" : "#1D2421");
        Set(resources, "SurfaceBackground", isLight ? "#EAEFEC" : "#242C28");
        Set(resources, "AppBorderBrush", isLight ? "#D8E0DC" : "#343E39");
        Set(resources, "TextPrimary", isLight ? "#17201D" : "#EEF4F1");
        Set(resources, "TextSecondary", isLight ? "#66756E" : "#9BACA3");
        Set(resources, "AccentBrush", "#087F56");
        Set(resources, "AccentSurfaceBrush", isLight ? "#E9F5F0" : "#19362D");
        Set(resources, "AccentHoverBrush", "#0A9565");
        Set(resources, "AccentPressedBrush", "#066744");
        Set(resources, "DangerBrush", "#C42B1C");
    }

    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Set(ResourceDictionary resources, string key, string color) =>
        resources[key] = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
}
