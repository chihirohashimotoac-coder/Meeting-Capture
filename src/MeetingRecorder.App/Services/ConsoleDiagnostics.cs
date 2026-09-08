using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MeetingRecorder.Audio;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Persistence;

namespace MeetingRecorder.App.Services;

/// <summary>
/// The <c>--diagnose</c> entry point: runs every self-check the application can
/// perform and prints a report, without opening a window.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the checks that genuinely require the user's own machine -
/// does WASAPI loopback actually capture this PC's audio, does the microphone
/// deliver samples, does this process hold any outbound connection - can be run
/// as one command instead of a manual procedure.
/// </para>
/// <para>
/// The executable is a WinExe, so it has no console of its own. Attaching to the
/// parent console makes <c>MeetingRecorder.exe --diagnose</c> behave like a
/// normal command-line tool when started from a terminal, and fall back to
/// writing a file when it is not.
/// </para>
/// </remarks>
public static class ConsoleDiagnostics
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    public static bool IsRequested(IReadOnlyList<string> args)
        => args.Any(a => string.Equals(a, "--diagnose", StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs the diagnostics and returns the process exit code.</summary>
    public static int Run(IReadOnlyList<string> args)
    {
        var attached = AttachConsole(AttachParentProcess);
        if (!attached)
        {
            // Started by double-click rather than from a terminal: give the
            // report somewhere to go instead of discarding it.
            attached = AllocConsole();
        }

        try
        {
            return Execute(args);
        }
        finally
        {
            if (attached)
            {
                FreeConsole();
            }
        }
    }

    private static int Execute(IReadOnlyList<string> args)
    {
        var seconds = ReadDouble(args, "--seconds", 5.0);
        var jsonPath = ReadString(args, "--json");

        using var services = new AppServices();

        var collector = new DiagnosticsCollector(
            services.DeviceProvider,
            services.CaptureFactory,
            services.ModelStore,
            services.Logger);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        var environment = OperatingSystem.IsWindows()
            ? ProcessNetworkInspector.DescribeEnvironment(version)
            : new DiagnosticEnvironment(version, RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(), false, null);

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine();
        Console.WriteLine($"音声取得テストを {seconds:F0} 秒間実行します。");
        Console.WriteLine("この間、マイクに向かって話し、PCで何か音声を再生してください。");
        Console.WriteLine();

        var report = collector.Collect(services.Settings, environment, seconds);
        var text = report.ToText();

        Console.WriteLine(text);

        // Always keep a copy next to the logs: a user who ran this by
        // double-clicking loses the console window as soon as it closes.
        var savedTo = SaveReport(text, report, jsonPath);
        Console.WriteLine($"レポートを保存しました: {savedTo}");

        return report.ExitCode;
    }

    private static string SaveReport(string text, DiagnosticReport report, string? jsonPath)
    {
        var directory = AppPaths.LogDirectory;
        Directory.CreateDirectory(directory);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var textPath = Path.Combine(directory, $"diagnose-{stamp}.txt");
        File.WriteAllText(textPath, text, new UTF8Encoding(true));

        if (!string.IsNullOrWhiteSpace(jsonPath))
        {
            var json = JsonSerializer.Serialize(report, JsonStore.Options);
            File.WriteAllText(jsonPath!, json, new UTF8Encoding(false));
        }

        return textPath;
    }

    private static double ReadDouble(IReadOnlyList<string> args, string name, double fallback)
    {
        var value = ReadString(args, name);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 0, 120)
            : fallback;
    }

    private static string? ReadString(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
