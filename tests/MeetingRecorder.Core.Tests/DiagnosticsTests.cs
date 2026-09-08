using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.ModelManagement;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Tests.TestSupport;
using Xunit;

namespace MeetingRecorder.Core.Tests;

/// <summary>
/// Exercises the `--diagnose` logic with synthetic devices.
/// </summary>
/// <remarks>
/// The diagnostics are the mechanism by which a user establishes, on their own
/// machine, the things CI cannot: that the microphone delivers samples, that
/// WASAPI loopback delivers the PC's audio, and that the process holds no
/// outbound connection. So the logic that classifies those outcomes has to be
/// right, and it is tested here against known inputs.
/// </remarks>
public sealed class DiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mr-diag-" + Guid.NewGuid().ToString("N"));

    public DiagnosticsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private AppSettings Settings() => new()
    {
        SaveRoot = _root,
        ModelDirectory = Path.Combine(_root, "models"),
        SttEnabled = true,
        Profile = new PerformanceProfile { SttModelId = ModelCatalog.WhisperBase },
    };

    private static DiagnosticEnvironment Environment(IReadOnlyList<string>? connections)
        => new("0.1.0", "Test OS", "X64", IsElevated: false, connections);

    private DiagnosticsCollector Collector(IAudioCaptureFactory factory, IAudioDeviceProvider devices)
        => new(devices, factory, new ModelStore(Path.Combine(_root, "models")));

    [Fact]
    public void ReportsASignalWhenBothEndpointsDeliverAudio()
    {
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, 48000, 0.3),
            SignalGenerator.Sine(660, 1.0, 48000, 0.3));

        var report = Collector(factory, new FakeDeviceProvider(2, 2))
            .Collect(Settings(), Environment(Array.Empty<string>()), captureSeconds: 0.7);

        var mic = report.Checks.Single(c => c.Id == "D-04");
        var system = report.Checks.Single(c => c.Id == "D-05");

        Assert.Equal(DiagnosticStatus.Ok, mic.Status);
        Assert.Equal(DiagnosticStatus.Ok, system.Status);
        Assert.Contains("信号を検出", mic.Detail);
        Assert.Contains("サンプル受信", system.Detail);
    }

    [Fact]
    public void DistinguishesASilentEndpointFromABrokenOne()
    {
        // The endpoint opens and delivers data, but every sample is zero - the
        // "muted device" case, which needs different advice from "no data".
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, 48000, 0.3),
            SignalGenerator.Silence(1.0, 48000));

        var report = Collector(factory, new FakeDeviceProvider(2, 2))
            .Collect(Settings(), Environment(Array.Empty<string>()), captureSeconds: 0.7);

        var system = report.Checks.Single(c => c.Id == "D-05");

        Assert.Equal(DiagnosticStatus.Warning, system.Status);
        Assert.Contains("無音", system.Detail);
        Assert.Contains("再生", system.Remedy);
    }

    [Fact]
    public void ADeniedMicrophoneIsToldToFixThePermissionRatherThanTheCable()
    {
        // Observed on a real Windows machine: the microphone enumerates, and the
        // refusal only arrives when the endpoint is opened. Telling that user to
        // reconnect the device sends them to fix something that is not broken.
        var factory = new FakeCaptureFactory(
            microphoneStartFailure: new UnauthorizedAccessException("Access is denied. (0x80070005 (E_ACCESSDENIED))"));

        var report = Collector(factory, new FakeDeviceProvider(1, 1))
            .Collect(Settings(), Environment(Array.Empty<string>()), captureSeconds: 0.4);

        var mic = report.Checks.Single(c => c.Id == "D-04");

        Assert.Equal(DiagnosticStatus.Error, mic.Status);
        Assert.Contains("プライバシー", mic.Remedy);
        Assert.DoesNotContain("接続し直し", mic.Remedy);

        // The user also needs to know the recording is not lost, only the mic leg.
        Assert.Contains("PC内部音声は記録されます", mic.Remedy);
    }

    [Fact]
    public void ReportsAnErrorWhenAnEndpointCannotBeOpened()
    {
        var report = Collector(new FakeCaptureFactory(failSystem: true), new FakeDeviceProvider(2, 0))
            .Collect(Settings(), Environment(Array.Empty<string>()), captureSeconds: 0.4);

        var system = report.Checks.Single(c => c.Id == "D-05");
        Assert.Equal(DiagnosticStatus.Error, system.Status);
        Assert.Equal(DiagnosticStatus.Error, report.Overall);
        Assert.Equal(1, report.ExitCode);

        var render = report.Checks.Single(c => c.Id == "D-02");
        Assert.Equal(DiagnosticStatus.Error, render.Status);
        Assert.Contains("再生デバイス", render.Remedy);
    }

    [Fact]
    public void AnEmptyConnectionListIsReportedAsNoOutboundTraffic()
    {
        var report = Collector(new FakeCaptureFactory(), new FakeDeviceProvider(1, 1))
            .Collect(Settings(), Environment(Array.Empty<string>()), captureSeconds: 0);

        var network = report.Checks.Single(c => c.Id == "D-08");
        Assert.Equal(DiagnosticStatus.Ok, network.Status);
        Assert.Contains("0 件", network.Detail);
    }

    [Fact]
    public void AnUnknownConnectionListIsReportedAsNotTestedRatherThanClean()
    {
        // Claiming "no connections" when the table could not be read would be
        // exactly the kind of false assurance this project must not produce.
        var report = Collector(new FakeCaptureFactory(), new FakeDeviceProvider(1, 1))
            .Collect(Settings(), Environment(null), captureSeconds: 0);

        var network = report.Checks.Single(c => c.Id == "D-08");
        Assert.Equal(DiagnosticStatus.NotTested, network.Status);
        Assert.Contains("T-45", network.Detail);
    }

    [Fact]
    public void OutboundConnectionsAreSurfacedAsAWarning()
    {
        var report = Collector(new FakeCaptureFactory(), new FakeDeviceProvider(1, 1))
            .Collect(Settings(), Environment(new[] { "203.0.113.7:443" }), captureSeconds: 0);

        var network = report.Checks.Single(c => c.Id == "D-08");
        Assert.Equal(DiagnosticStatus.Warning, network.Status);
        Assert.Contains("203.0.113.7:443", network.Detail);
    }

    [Fact]
    public void EveryNonOkCheckExplainsWhatToDo()
    {
        var report = Collector(new FakeCaptureFactory(failMicrophone: true, failSystem: true), new FakeDeviceProvider(0, 0))
            .Collect(new AppSettings { SttEnabled = true }, Environment(null), captureSeconds: 0.3);

        foreach (var check in report.Checks.Where(c =>
                     c.Status is DiagnosticStatus.Error or DiagnosticStatus.Warning))
        {
            Assert.False(string.IsNullOrWhiteSpace(check.Remedy), $"{check.Id} ({check.Title}) has no remedy");
        }
    }

    [Fact]
    public void TheTextReportIncludesEveryCheckAndAVerdict()
    {
        var report = Collector(new FakeCaptureFactory(), new FakeDeviceProvider(1, 1))
            .Collect(Settings(), Environment(Array.Empty<string>()), captureSeconds: 0);

        var text = report.ToText();

        Assert.Contains("MeetingRecorder 診断レポート", text);
        Assert.Contains("総合判定", text);
        foreach (var check in report.Checks)
        {
            Assert.Contains(check.Id, text);
        }

        // The report must not overstate what it covered.
        Assert.Contains("docs/WINDOWS_E2E_TEST.md", text);
    }

    [Fact]
    public void SkippingTheCaptureProbeIsRecordedAsNotTested()
    {
        var report = Collector(new FakeCaptureFactory(), new FakeDeviceProvider(1, 1))
            .Collect(Settings(), Environment(Array.Empty<string>()), captureSeconds: 0);

        var skipped = report.Checks.Single(c => c.Id == "D-04/05");
        Assert.Equal(DiagnosticStatus.NotTested, skipped.Status);
        Assert.DoesNotContain(report.Checks, c => c.Id == "D-04");
    }

    private sealed class FakeDeviceProvider : IAudioDeviceProvider
    {
        private readonly int _captureCount;
        private readonly int _renderCount;

        public FakeDeviceProvider(int captureCount, int renderCount)
        {
            _captureCount = captureCount;
            _renderCount = renderCount;
        }

        public IReadOnlyList<AudioDeviceInfo> GetCaptureDevices()
            => Build(_captureCount, AudioSourceKind.Microphone, "Fake mic");

        public IReadOnlyList<AudioDeviceInfo> GetRenderDevices()
            => Build(_renderCount, AudioSourceKind.SystemAudio, "Fake speakers");

        private static IReadOnlyList<AudioDeviceInfo> Build(int count, AudioSourceKind kind, string prefix)
            => Enumerable.Range(0, count)
                .Select(i => new AudioDeviceInfo($"id-{i}", $"{prefix} {i}", i == 0, kind))
                .ToList();
    }
}
