using System.IO;
using System.Text.Json;

namespace PrivChat.Overlay;

internal sealed record LocalLlmConfig(string Endpoint)
{
    // llama-server default from the local setup's START HERE.bat (OpenAI-compatible, no key).
    public const string DefaultEndpoint = "http://127.0.0.1:1234";

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PrivChat", "llm-config.json");

    // Never returns null: an absent or broken file falls back to the default endpoint,
    // so the LLM tab works with zero setup.
    public static LocalLlmConfig TryLoad()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var endpoint = JsonSerializer
                    .Deserialize<LocalLlmConfig>(File.ReadAllText(ConfigPath))?
                    .Endpoint?
                    .Trim()
                    .TrimEnd('/');
                if (!string.IsNullOrEmpty(endpoint))
                {
                    return new LocalLlmConfig(endpoint!);
                }
            }
        }
        catch
        {
            // Fall through to the default.
        }

        return new LocalLlmConfig(DefaultEndpoint);
    }

    public static void Save(LocalLlmConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config));
    }
}
