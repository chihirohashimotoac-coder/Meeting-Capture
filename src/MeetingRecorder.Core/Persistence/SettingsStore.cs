using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Persistence;

/// <summary>Loads and saves <see cref="AppSettings"/>. No cloud sync, ever.</summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _sync = new();

    public SettingsStore(string? path = null, ILogger? logger = null)
    {
        _path = path ?? AppPaths.SettingsFile;
        _logger = logger ?? NullLogger.Instance;
    }

    public string Path => _path;

    public AppSettings Load()
    {
        lock (_sync)
        {
            var settings = JsonStore.Read<AppSettings>(_path);
            if (settings is null)
            {
                _logger.Info(nameof(SettingsStore), "No settings file found; starting with defaults.");
                return CreateDefault();
            }

            if (settings.Version != AppSettings.CurrentVersion)
            {
                _logger.Warn(nameof(SettingsStore), $"Settings version {settings.Version} migrated to {AppSettings.CurrentVersion}.");
                settings.Version = AppSettings.CurrentVersion;
            }

            settings.Processing ??= new AudioProcessingSettings();
            return settings;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_sync)
        {
            try
            {
                JsonStore.WriteAtomic(_path, settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Error(nameof(SettingsStore), "Failed to persist settings.", ex);
            }
        }
    }

    public static AppSettings CreateDefault() => new()
    {
        SaveRoot = AppPaths.DefaultSaveRoot(),
        ModelDirectory = AppPaths.ModelDirectory,
    };
}
