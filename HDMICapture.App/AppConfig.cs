using System;
using System.IO;
using System.Text.Json;

namespace HDMICapture.App
{
    public class AppSettings
    {
        public string MeteorOutputDirectory { get; set; } = "meteor_output";
        public string NdiName { get; set; } = "HDMI Capture";
        public bool AudioEnabled { get; set; } = true;
        public int ExposureSeconds { get; set; } = 4;
        public string? MaskFilePath { get; set; }
        public double OverlayFontScale { get; set; } = 1.0;
        public int OverlayMarginX { get; set; } = 12;
        public int OverlayMarginY { get; set; } = 12;
        public string OverlayFontFace { get; set; } = "HersheySimplex";
        public bool MeteorDetectionEnabled { get; set; } = true;
    }

    public static class AppConfig
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HDMICapture");
        private static readonly string ConfigPath = Path.Combine(ConfigDir, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null) return settings;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"設定読み込みエラー: {ex.Message}");
            }

            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"設定保存エラー: {ex.Message}");
            }
        }
    }
}
