using System;
using System.IO;
using System.Text.Json;

namespace AiSubtitlePro.Infrastructure.AI;

public static class OpenRouterConfig
{
    private sealed class ConfigModel
    {
        public string? ApiKey { get; set; }
        public string Model { get; set; } = "deepseek/deepseek-r1:free";
    }

    private static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AiSubtitlePro");

    private static string ConfigPath => Path.Combine(ConfigDir, "openrouter.json");

    public static (string? ApiKey, string Model) Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return (null, "deepseek/deepseek-r1:free");

            var json = File.ReadAllText(ConfigPath);
            var model = JsonSerializer.Deserialize<ConfigModel>(json);
            if (model == null)
                return (null, "deepseek/deepseek-r1:free");

            return (string.IsNullOrWhiteSpace(model.ApiKey) ? null : model.ApiKey, string.IsNullOrWhiteSpace(model.Model) ? "deepseek/deepseek-r1:free" : model.Model);
        }
        catch
        {
            return (null, "deepseek/deepseek-r1:free");
        }
    }

    public static void Save(string? apiKey, string? model = null)
    {
        Directory.CreateDirectory(ConfigDir);
        var m = new ConfigModel
        {
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
            Model = string.IsNullOrWhiteSpace(model) ? "deepseek/deepseek-r1:free" : model
        };
        var json = JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}
