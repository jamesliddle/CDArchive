using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CDArchive.Core.Services;

/// <summary>
/// User-mutable application settings (archive root, ffmpeg path, audio
/// preferences). Persisted as JSON under
/// <c>%AppData%\CDArchive\settings.json</c>.
///
/// <para>
/// Constructor is I/O-free — properties hold sensible defaults until
/// <see cref="Initialize"/> is called. <c>App.OnStartup</c> calls
/// <c>Initialize</c> once after the DI container is built, before any
/// consumer reads the values. The old design read from disk in the
/// constructor, which (a) blocked DI container build on a slow / locked
/// filesystem, (b) only caught <see cref="JsonException"/> — every other
/// I/O failure (permission denied, file locked by AV, etc.) crashed the
/// app on startup with no recovery path other than deleting the file
/// blind. See Rework H4.
/// </para>
///
/// <para>
/// <see cref="Save"/> writes atomically via temp-then-rename so a process
/// kill mid-write can't truncate the live file. The user's archive root +
/// ffmpeg path + player preferences survive an interrupted save.
/// </para>
/// </summary>
public class ArchiveSettings : IArchiveSettings
{
    private static readonly string DefaultSettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CDArchive");

    private static readonly string DefaultSettingsFilePath =
        Path.Combine(DefaultSettingsDirectory, "settings.json");

    private readonly string _settingsFilePath;
    private readonly ILogger<ArchiveSettings> _logger;

    public string ArchiveRootPath { get; set; } = @"D:\CD archive";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public int Mp3Bitrate { get; set; } = 320;
    public PreferredAudioFormat PreferredAudioFormat { get; set; } = PreferredAudioFormat.Flac;
    public float PlayerVolume { get; set; } = 1.0f;

    /// <summary>
    /// Production ctor used by DI. Reads no I/O — the host must call
    /// <see cref="Initialize"/> once before consumers read property values.
    /// </summary>
    public ArchiveSettings(ILogger<ArchiveSettings>? logger = null)
        : this(DefaultSettingsFilePath, logger) { }

    /// <summary>
    /// Testable ctor. Takes an explicit settings-file path so unit tests can
    /// point at a temp file without touching the production AppData folder.
    /// </summary>
    internal ArchiveSettings(string settingsFilePath, ILogger<ArchiveSettings>? logger = null)
    {
        _settingsFilePath = settingsFilePath;
        _logger = logger ?? NullLogger<ArchiveSettings>.Instance;
    }

    /// <summary>
    /// Reads <c>settings.json</c> (if present) and applies the persisted
    /// values to the property defaults. Any failure — missing file, corrupt
    /// JSON, permission denied, file locked by another process — is logged
    /// and swallowed; the defaults survive intact. Safe to call multiple
    /// times.
    /// </summary>
    public void Initialize()
    {
        if (!File.Exists(_settingsFilePath))
        {
            _logger.LogDebug("Settings file not found at {Path}; using defaults", _settingsFilePath);
            return;
        }

        try
        {
            var json = File.ReadAllText(_settingsFilePath);
            // Parse to a JsonDocument first so we can extract each setting
            // independently — a single bad value (e.g. an unknown
            // PreferredAudioFormat that the JsonStringEnumConverter would
            // throw on) used to fail the whole deserialization and reset
            // every setting back to default. See Rework M10.
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (TryReadString(root, nameof(SettingsData.ArchiveRootPath)) is { } archive)
                ArchiveRootPath = archive;
            if (TryReadString(root, nameof(SettingsData.FfmpegPath)) is { } ffmpeg)
                FfmpegPath = ffmpeg;
            if (TryReadInt(root, nameof(SettingsData.Mp3Bitrate)) is int bitrate && bitrate > 0)
                Mp3Bitrate = bitrate;
            if (TryReadAudioFormat(root, nameof(SettingsData.PreferredAudioFormat)) is { } format)
                PreferredAudioFormat = format;
            if (TryReadFloat(root, nameof(SettingsData.PlayerVolume)) is float v)
                PlayerVolume = Math.Clamp(v, 0f, 1f);
        }
        catch (Exception ex)
        {
            // Pre-fix this was `catch (JsonException)` only — any IOException
            // / UnauthorizedAccessException / file-locked-by-AV scenario would
            // crash the app on startup. Widen to Exception so users keep a
            // working app with default settings; the failure is logged so a
            // genuine misconfig still surfaces in cdarchive-YYYYMMDD.log.
            _logger.LogWarning(ex,
                "Failed to load settings from {Path}; keeping defaults", _settingsFilePath);
        }
    }

    private static string? TryReadString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static int? TryReadInt(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var el)) return null;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v) ? v : null;
    }

    private static float? TryReadFloat(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var el)) return null;
        return el.ValueKind == JsonValueKind.Number && el.TryGetSingle(out var v) ? v : null;
    }

    /// <summary>
    /// M10: tolerant parse of <see cref="PreferredAudioFormat"/>. Accepts both
    /// string forms ("Flac" / "Mp3") and integer forms (the original JSON
    /// shape). Unknown / malformed values return null so the caller keeps the
    /// existing default — pre-fix any unknown string here would throw and
    /// reset every other setting back to default too. Logs a warning on
    /// unparseable input so a misconfiguration shows up in the rolling log.
    /// </summary>
    private PreferredAudioFormat? TryReadAudioFormat(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var el)) return null;

        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                var s = el.GetString();
                if (Enum.TryParse<PreferredAudioFormat>(s, ignoreCase: true, out var parsed))
                    return parsed;
                _logger.LogWarning(
                    "Settings: unknown PreferredAudioFormat value \"{Value}\"; keeping default", s);
                return null;
            case JsonValueKind.Number:
                if (el.TryGetInt32(out var n) && Enum.IsDefined(typeof(PreferredAudioFormat), n))
                    return (PreferredAudioFormat)n;
                _logger.LogWarning(
                    "Settings: PreferredAudioFormat integer {Value} is out of range; keeping default", el);
                return null;
            default:
                _logger.LogWarning(
                    "Settings: PreferredAudioFormat has unexpected JSON kind {Kind}; keeping default", el.ValueKind);
                return null;
        }
    }

    /// <summary>
    /// Persists the current property values to <c>settings.json</c> using a
    /// temp-then-rename atomic write. Any failure between writing the temp
    /// file and renaming it into place leaves the live file untouched. The
    /// temp file is best-effort deleted on failure.
    /// </summary>
    public void Save()
    {
        var dir = Path.GetDirectoryName(_settingsFilePath)
            ?? throw new InvalidOperationException(
                $"Settings path '{_settingsFilePath}' has no directory component.");

        Directory.CreateDirectory(dir);

        // Serialize the enum as its string form ("Flac" / "Mp3") so a user
        // who opens settings.json by hand sees the meaningful name instead
        // of an opaque integer. Read paths accept both forms via M10's
        // tolerant parse.
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };
        var json = JsonSerializer.Serialize(new SettingsData
        {
            ArchiveRootPath = ArchiveRootPath,
            FfmpegPath = FfmpegPath,
            Mp3Bitrate = Mp3Bitrate,
            PreferredAudioFormat = PreferredAudioFormat,
            PlayerVolume = PlayerVolume
        }, options);

        // Atomic write: temp sibling in the same directory (same volume → atomic
        // File.Move), then File.Move(tmp, real, overwrite: true). A crash
        // between the WriteAllText and the Move leaves the live file
        // untouched. Pre-fix this was a single `File.WriteAllText(SettingsFilePath, json)`
        // — a process kill mid-write truncated the live file and the user
        // lost their archive root + ffmpeg path on next launch.
        var tmp = _settingsFilePath + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, _settingsFilePath, overwrite: true);
        }
        catch
        {
            // Best-effort cleanup of the stale temp. Swallow any failure here
            // — the original exception is more informative.
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception cleanupEx)
            {
                _logger.LogDebug(cleanupEx, "Failed to delete temp settings file {Path}", tmp);
            }
            throw;
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
