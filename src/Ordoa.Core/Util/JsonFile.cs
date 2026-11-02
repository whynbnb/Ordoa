using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Ordoa.Core.Util;

public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        TypeInfoResolver = CoreJsonContext.Default,
    };

    private static JsonTypeInfo<T> TypeInfo<T>()
        => (JsonTypeInfo<T>?)Options.GetTypeInfo(typeof(T))
           ?? throw new InvalidOperationException($"No JSON metadata registered for {typeof(T)}.");

    public static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize(json, TypeInfo<T>());
        }
        catch
        {
            return null;
        }
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = JsonSerializer.Serialize(value, TypeInfo<T>());
        var temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}
