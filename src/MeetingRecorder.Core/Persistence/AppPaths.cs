namespace MeetingRecorder.Core.Persistence;

/// <summary>
/// Decides where settings, logs and AI models live.
/// </summary>
/// <remarks>
/// The application is distributed as a ZIP that the user unpacks anywhere, so
/// "portable first" is the rule: if the folder next to the executable is
/// writable, everything stays there and the app leaves no trace elsewhere. If it
/// is not - the ZIP was unpacked into Program Files, or onto a read-only share -
/// state falls back to %LOCALAPPDATA%, which a standard user can always write.
/// Neither path needs administrator rights.
/// </remarks>
public static class AppPaths
{
    public const string AppFolderName = "MeetingRecorder";

    /// <summary>Directory holding the executable.</summary>
    public static string InstallDirectory { get; } = ResolveInstallDirectory();

    private static readonly Lazy<string> DataRootLazy = new(ResolveDataRoot);

    /// <summary>Root for settings, logs and models.</summary>
    public static string DataRoot => DataRootLazy.Value;

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");

    public static string LogDirectory => Path.Combine(DataRoot, "logs");

    public static string ModelDirectory => Path.Combine(DataRoot, "models");

    /// <summary>Default meeting output folder, used until the user picks one.</summary>
    public static string DefaultSaveRoot()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = DataRoot;
        }

        return Path.Combine(documents, AppFolderName);
    }

    /// <summary>True when the portable location is being used.</summary>
    public static bool IsPortable => string.Equals(
        Path.GetFullPath(DataRoot).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(Path.Combine(InstallDirectory, "data")).TrimEnd(Path.DirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);

    private static string ResolveInstallDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory;
        return string.IsNullOrWhiteSpace(baseDirectory) ? Directory.GetCurrentDirectory() : baseDirectory;
    }

    private static string ResolveDataRoot()
    {
        var portable = Path.Combine(InstallDirectory, "data");
        if (CanWrite(portable))
        {
            return portable;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        var roaming = Path.Combine(localAppData, AppFolderName);
        Directory.CreateDirectory(roaming);
        return roaming;
    }

    /// <summary>Probe a directory by actually creating and deleting a file in it.</summary>
    public static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
