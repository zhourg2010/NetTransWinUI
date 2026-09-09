using System.Text.Json;

namespace NetTrans.Verify;

/// <summary>
/// Reading and writing the small JSON files NetTrans keeps for itself.
///
/// Two rules, and both exist because these files are written while downloads
/// are finishing and the app is being closed: a half-written file must never
/// replace a good one, and a file that did get truncated must cost the history
/// rather than the app.
/// </summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads the file, or hands back null when it is missing, empty, corrupt or from some other future.</summary>
    public static T? Read<T>(string path, JsonSerializerOptions? options = null) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options ?? Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Writes through a temporary file in the same directory, then moves it into place.</summary>
    public static void Write<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, options ?? Options));
        File.Move(temporary, path, overwrite: true);
    }
}
