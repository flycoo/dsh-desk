using System.Text.Json;
using DshDesk.Models;

namespace DshDesk.Services;

public sealed class SettingsStore
{
    public const string DefaultSettingsPath = @"G:\DeepSeekHarness\.dsh-desk\settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public SettingsStore(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? DefaultSettingsPath;
    }

    public string SettingsPath { get; }

    public DshSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                var defaults = new DshSettings();
                Save(defaults);
                return defaults;
            }

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<DshSettings>(json, JsonOptions) ?? new DshSettings();
        }
        catch
        {
            return new DshSettings();
        }
    }

    public void Save(DshSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, true);
    }
}
