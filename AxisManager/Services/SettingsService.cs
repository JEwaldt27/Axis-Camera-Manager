using System;
using System.IO;
using System.Text.Json;

namespace AxisManager.Services;

public class AppSettings
{
    public string DefaultUsername { get; set; } = "root";
    public string DefaultPassword { get; set; } = "";
    public bool AutoConnect { get; set; } = true;
    public string Theme { get; set; } = "Dark";   // "Dark" or "Light"
}

public static class SettingsService
{
    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AxisManager", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_path, json);
        }
        catch { }
    }
}