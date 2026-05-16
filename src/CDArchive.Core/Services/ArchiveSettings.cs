using System.Text.Json;

namespace CDArchive.Core.Services;

public class ArchiveSettings : IArchiveSettings
{
    private static readonly string SettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CDArchive");

    private static readonly string SettingsFilePath =
        Path.Combine(SettingsDirectory, "settings.json");

    public string ArchiveRootPath { get; set; } = @"D:\CD archive";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public int Mp3Bitrate { get; set; } = 320;
    public PreferredAudioFormat PreferredAudioFormat { get; set; } = PreferredAudioFormat.Flac;
    public float PlayerVolume { get; set; } = 1.0f;

    public ArchiveSettings()
    {
        Load();
    }

    public void Save()
    {
        if (!Directory.Exists(SettingsDirectory))
            Directory.CreateDirectory(SettingsDirectory);

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(new SettingsData
        {
            ArchiveRootPath = ArchiveRootPath,
            FfmpegPath = FfmpegPath,
            Mp3Bitrate = Mp3Bitrate,
            PreferredAudioFormat = PreferredAudioFormat,
            PlayerVolume = PlayerVolume
        }, options);

        File.WriteAllText(SettingsFilePath, json);
    }

    public void Load()
    {
        if (!File.Exists(SettingsFilePath))
            return;

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            var data = JsonSerializer.Deserialize<SettingsData>(json);
            if (data is not null)
            {
                ArchiveRootPath = data.ArchiveRootPath ?? ArchiveRootPath;
                FfmpegPath = data.FfmpegPath ?? FfmpegPath;
                Mp3Bitrate = data.Mp3Bitrate > 0 ? data.Mp3Bitrate : Mp3Bitrate;
                PreferredAudioFormat = data.PreferredAudioFormat ?? PreferredAudioFormat;
                if (data.PlayerVolume is float v)
                    PlayerVolume = Math.Clamp(v, 0f, 1f);
            }
        }
        catch (JsonException)
        {
            // If the file is corrupt, keep defaults
        }
    }

    private class SettingsData
    {
        public string? ArchiveRootPath { get; set; }
        public string? FfmpegPath { get; set; }
        public int Mp3Bitrate { get; set; }
        public PreferredAudioFormat? PreferredAudioFormat { get; set; }
        public float? PlayerVolume { get; set; }
    }
}
