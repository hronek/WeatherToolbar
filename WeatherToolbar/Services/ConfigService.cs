
using System;
using System.IO;
using System.Text.Json;

namespace WeatherToolbar.Services
{
    public class AppConfig
    {
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public int? RefreshMinutes { get; set; }
        public string City { get; set; }
        public string Country { get; set; }
        public string FontFamily { get; set; }
        public int? FontSize { get; set; }
        public bool? ShowGlyph { get; set; }
        public int? OutlineRadius { get; set; }
        public bool? EnableLogging { get; set; }
    }

    public static class ConfigService
    {
        public static string ConfigDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeatherToolbar");
        public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

        public static AppConfig Load()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    // Default: Písek, CZ
                    var def = new AppConfig
                    {
                        Latitude = 49.3104950,
                        Longitude = 14.1414903,
                        City = "Písek",
                        Country = "Česko",
                        RefreshMinutes = 1,
                        ShowGlyph = true,
                        FontSize = 16,
                        OutlineRadius = 1,
                        EnableLogging = false
                    };
                    Save(def); // persist so it stays fixed until user clears
                    return def;
                }
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (cfg == null) return new AppConfig();
                // Backfill missing FontSize to 16 and persist
                if (!cfg.FontSize.HasValue)
                {
                    cfg.FontSize = 16;
                    Save(cfg);
                }
                if (!cfg.OutlineRadius.HasValue)
                {
                    cfg.OutlineRadius = 1;
                    Save(cfg);
                }
                if (!cfg.EnableLogging.HasValue)
                {
                    cfg.EnableLogging = false;
                    Save(cfg);
                }
                return cfg;
            }
            catch
            {
                return new AppConfig();
            }
        }

        public static bool IsLoggingEnabled()
        {
            try
            {
                var cfg = Load();
                return cfg.EnableLogging ?? false;
            }
            catch { return false; }
        }

        public static void Save(AppConfig cfg)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }
    }
}
