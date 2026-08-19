using System.IO;
using System.Text.Json;

namespace PrivChat.Overlay;

internal sealed record AiConfig(string ApiKey, string Model)
{
    public const string DefaultModel = "gemini-2.5-flash";

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PrivChat", "ai-config.json");

    public static AiConfig? TryLoad()
    {
        // Case-insensitive so a hand-edited {"apiKey": ...} file still binds.
        var readOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        AiConfig? saved = null;
        try
        {
            if (File.Exists(ConfigPath))
            {
                saved = JsonSerializer.Deserialize<AiConfig>(File.ReadAllText(ConfigPath), readOptions);
            }
        }
        catch
        {
            saved = null;
        }

        var key = saved?.ApiKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            key = Environment.GetEnvironmentVariable("GEMINI_API_KEY")?.Trim();
        }

        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return new AiConfig(key, NormalizeModel(saved?.Model));
    }

    public static void Save(AiConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new AiConfig(config.ApiKey, NormalizeModel(config.Model))));
    }

    public static string NormalizeModel(string? model)
    {
        model = model?.Trim();
        if (string.IsNullOrEmpty(model))
        {
            return DefaultModel;
        }

        const string prefix = "models/";
        if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            model = model[prefix.Length..];
        }

        return string.IsNullOrEmpty(model) ? DefaultModel : model;
    }
}
