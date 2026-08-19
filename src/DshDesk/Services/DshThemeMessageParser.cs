using System.Text.Json;

namespace DshDesk.Services;

public static class DshThemeMessageParser
{
    public static bool TryParse(string json, out bool isLight)
    {
        isLight = false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("source", out var source) ||
                source.GetString() != "dsh-desk-theme" ||
                !root.TryGetProperty("theme", out var theme))
            {
                return false;
            }

            switch (theme.GetString())
            {
                case "light":
                    isLight = true;
                    return true;
                case "dark":
                    return true;
                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
