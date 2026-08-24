using System.Text.Json;
using NetTrans.Models;

namespace NetTrans.Services;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    /// <summary>
    /// Portable by default: the 设置 sheet promises settings land next to the
    /// executable and nothing is written to the registry. If that directory is
    /// read-only (Program Files, a mounted image), fall back to LocalAppData
    /// rather than losing every change.
    /// </summary>
    public JsonSettingsStore()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "NetTrans.settings.json");
        if (IsWritable(AppContext.BaseDirectory))
        {
            _filePath = beside;
            return;
        }

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetTrans");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "settings.json");
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".nettrans-write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is not null) return settings;
            }
        }
        catch (Exception)
        {
            // Corrupt/partial settings file — fall back to defaults rather than crash the app.
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
