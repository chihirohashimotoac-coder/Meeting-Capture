using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;
using MeetingRecorder.Core.Pipeline;
using MeetingRecorder.Core.Tests.TestSupport;
using Xunit;

namespace MeetingRecorder.Core.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mr-persist-" + Guid.NewGuid().ToString("N"));

    public PersistenceTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void SettingsRoundTripIncludingJapaneseText()
    {
        var path = Path.Combine(_root, "settings.json");
        var store = new SettingsStore(path);

        var settings = SettingsStore.CreateDefault();
        settings.MicrophoneDeviceName = "マイク配列 (Realtek)";
        settings.SaveRoot = Path.Combine(_root, "会議");
        settings.Format = RecordingFormat.Mp3;
        settings.Mp3BitrateKbps = 128;
        settings.SetupCompleted = true;
        store.Save(settings);

        var loaded = store.Load();
        Assert.Equal("マイク配列 (Realtek)", loaded.MicrophoneDeviceName);
        Assert.Equal(RecordingFormat.Mp3, loaded.Format);
        Assert.Equal(128, loaded.Mp3BitrateKbps);
        Assert.True(loaded.SetupCompleted);

        // Japanese must stay readable in the file itself, not be escaped.
        Assert.Contains("マイク配列", File.ReadAllText(path));
    }

    [Fact]
    public void MissingSettingsFileYieldsUsableDefaults()
    {
        var store = new SettingsStore(Path.Combine(_root, "absent.json"));
        var settings = store.Load();

        Assert.False(settings.SetupCompleted);
        Assert.NotNull(settings.Processing);
        Assert.Equal("ja", settings.SttLanguage);
    }

    [Fact]
    public void CorruptSettingsFileFallsBackToDefaultsInsteadOfCrashing()
    {
        var path = Path.Combine(_root, "broken.json");
        File.WriteAllText(path, "{ this is not json");

        var settings = new SettingsStore(path).Load();
        Assert.False(settings.SetupCompleted);
    }

    [Fact]
    public void MeetingFolderNameFollowsTheSpecifiedPattern()
    {
        var name = MeetingFolder.BuildFolderName(new DateTimeOffset(2026, 9, 7, 14, 30, 0, TimeSpan.FromHours(9)), null);
        Assert.Equal("2026-09-07_1430_Meeting", name);
    }

    [Fact]
    public void MeetingFolderNameSanitisesUserSuppliedTitles()
    {
        var name = MeetingFolder.BuildFolderName(
            new DateTimeOffset(2026, 9, 7, 14, 30, 0, TimeSpan.FromHours(9)),
            "月次:定例/会議*");

        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('*', name);
        Assert.StartsWith("2026-09-07_1430_", name);
    }

    [Fact]
    public void CollidingFolderNamesGetASuffix()
    {
        var when = DateTimeOffset.Now;
        var first = MeetingFolder.Create(_root, when);
        var second = MeetingFolder.Create(_root, when);

        Assert.NotEqual(first.Path, second.Path);
        Assert.True(Directory.Exists(second.Path));
    }

    [Fact]
    public void TranscriptExportContainsTimestampsSourcesAndText()
    {
        var metadata = new MeetingMetadata
        {
            FolderName = "2026-09-07_1430_Meeting",
            StartedAtLocal = new DateTimeOffset(2026, 9, 7, 14, 30, 0, TimeSpan.FromHours(9)),
            DurationSeconds = 3723,
            MicrophoneDeviceName = "マイク",
            RenderDeviceName = "スピーカー",
            SttModelId = "whisper-small-q5_1",
        };

        var segments = new List<TranscriptSegment>
        {
            new() { StartMs = 84_000, EndMs = 88_000, Source = AudioSourceKind.Microphone, Text = "今回のKPIについて確認します。" },
            new() { StartMs = 91_000, EndMs = 96_000, Source = AudioSourceKind.SystemAudio, SpeakerId = 0, Text = "はい。前月実績についてですが。" },
        };

        var text = TranscriptExporter.BuildText(metadata, segments);
        Assert.Contains("[00:01:24] [マイク]", text);
        Assert.Contains("今回のKPIについて確認します。", text);
        Assert.Contains("[00:01:31] [PC音声 / Speaker 1]", text);
        Assert.Contains("01:02:03", text);

        var markdown = TranscriptExporter.BuildMarkdown(metadata, segments);
        Assert.Contains("# 文字起こし", markdown);
        Assert.Contains("**[00:01:24] マイク**", markdown);
        Assert.Contains("whisper-small-q5_1", markdown);
    }

    [Fact]
    public void JournalReplayReconstructsEverySegmentEvenAfterATornLastLine()
    {
        var folder = MeetingFolder.Create(_root, DateTimeOffset.Now);
        var metadata = new MeetingMetadata { FolderName = folder.Name, StartedAtLocal = DateTimeOffset.Now };

        using (var journal = new RecoveryJournal(folder.JournalPath))
        {
            journal.WriteHeader(metadata);
            for (var i = 0; i < 10; i++)
            {
                journal.AppendSegment(new TranscriptSegment
                {
                    Id = $"segment-{i}",
                    StartMs = i * 1000,
                    EndMs = (i * 1000) + 900,
                    Source = i % 2 == 0 ? AudioSourceKind.Microphone : AudioSourceKind.SystemAudio,
                    Text = $"発言{i}",
                });
            }

            journal.Flush();
        }

        // Simulate a power cut in the middle of writing one more line.
        File.AppendAllText(folder.JournalPath, "{\"kind\":\"Segment\",\"segm");

        var entries = RecoveryJournal.Read(folder.JournalPath);
        var segments = CrashRecoveryService.RebuildSegments(entries);

        Assert.Equal(10, segments.Count);
        Assert.Equal("発言0", segments[0].Text);
        Assert.Equal("発言9", segments[9].Text);
    }

    [Fact]
    public void JournalUpdatesOverwriteTheOriginalSegment()
    {
        var folder = MeetingFolder.Create(_root, DateTimeOffset.Now);
        using (var journal = new RecoveryJournal(folder.JournalPath))
        {
            var segment = new TranscriptSegment { Id = "a", StartMs = 0, Text = "誤変換" };
            journal.AppendSegment(segment);

            segment.Text = "修正済み";
            segment.IsEdited = true;
            journal.UpdateSegment(segment);
        }

        var segments = CrashRecoveryService.RebuildSegments(RecoveryJournal.Read(folder.JournalPath));
        var only = Assert.Single(segments);
        Assert.Equal("修正済み", only.Text);
        Assert.True(only.IsEdited);
    }

    [Fact]
    public void CrashScanFindsAnInterruptedMeetingAndRebuildsItsDeliverables()
    {
        var folder = MeetingFolder.Create(_root, DateTimeOffset.Now);
        var metadata = new MeetingMetadata
        {
            FolderName = folder.Name,
            StartedAtLocal = DateTimeOffset.Now.AddMinutes(-30),
            MicrophoneDeviceName = "マイク",
        };

        folder.WriteMetadata(metadata);

        using (var writer = new WavFileWriter(folder.WavPath, 48000))
        {
            writer.Write(SignalGenerator.Sine(300, 2.0, 48000, 0.3));
        }

        using (var journal = new RecoveryJournal(folder.JournalPath))
        {
            journal.WriteHeader(metadata);
            journal.AppendSegment(new TranscriptSegment { Id = "x", StartMs = 500, EndMs = 1500, Text = "復旧テスト", Source = AudioSourceKind.Microphone });
            journal.Flush();
        }

        var service = new CrashRecoveryService();
        var recoverable = service.Scan(_root);

        var meeting = Assert.Single(recoverable);
        Assert.Equal(1, meeting.SegmentCount);
        Assert.InRange(meeting.AudioSeconds, 1.9, 2.1);

        var recovered = service.Recover(meeting);

        Assert.True(recovered.RecoveredFromCrash);
        Assert.InRange(recovered.DurationSeconds, 1.9, 2.1);
        Assert.True(File.Exists(folder.TranscriptTextPath));
        Assert.Contains("復旧テスト", File.ReadAllText(folder.TranscriptMarkdownPath));
        Assert.False(File.Exists(folder.JournalPath));
        Assert.Empty(service.Scan(_root));
    }

    [Fact]
    public void ACompletedMeetingIsNotOfferedForRecovery()
    {
        var folder = MeetingFolder.Create(_root, DateTimeOffset.Now);
        using (var journal = new RecoveryJournal(folder.JournalPath))
        {
            journal.WriteHeader(new MeetingMetadata { FolderName = folder.Name });
            journal.MarkCompleted();
        }

        Assert.Empty(new CrashRecoveryService().Scan(_root));
    }

    [Fact]
    public void DismissingRecoveryKeepsTheAudio()
    {
        var folder = MeetingFolder.Create(_root, DateTimeOffset.Now);
        using (var writer = new WavFileWriter(folder.WavPath, 48000))
        {
            writer.Write(new float[48000]);
        }

        using (var journal = new RecoveryJournal(folder.JournalPath))
        {
            journal.WriteHeader(new MeetingMetadata { FolderName = folder.Name });
            journal.Flush();
        }

        var service = new CrashRecoveryService();
        service.Dismiss(service.Scan(_root).Single());

        Assert.True(File.Exists(folder.WavPath));
        Assert.False(File.Exists(folder.JournalPath));
    }

    [Fact]
    public void AtomicWriteLeavesNoTemporaryFileBehind()
    {
        var path = Path.Combine(_root, "atomic.json");
        JsonStore.WriteAtomic(path, new MeetingMetadata { FolderName = "a" });
        JsonStore.WriteAtomic(path, new MeetingMetadata { FolderName = "b" });

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal("b", JsonStore.Read<MeetingMetadata>(path)!.FolderName);
    }
}

public class TranscriptStoreTests
{
    [Fact]
    public void KeepsSegmentsOrderedByStartTimeRegardlessOfArrivalOrder()
    {
        var store = new TranscriptStore();
        store.Add(new TranscriptSegment { StartMs = 5000, Text = "後" });
        store.Add(new TranscriptSegment { StartMs = 1000, Text = "先" });
        store.Add(new TranscriptSegment { StartMs = 3000, Text = "中" });

        var segments = store.Snapshot();
        Assert.Equal(new[] { "先", "中", "後" }, segments.Select(s => s.Text));
    }

    [Fact]
    public void RenamingASpeakerAppliesToEveryMatchingSegment()
    {
        var store = new TranscriptStore();
        for (var i = 0; i < 3; i++)
        {
            store.Add(new TranscriptSegment { StartMs = i * 1000, Source = AudioSourceKind.SystemAudio, SpeakerId = 0, Text = $"a{i}" });
        }

        store.Add(new TranscriptSegment { StartMs = 9000, Source = AudioSourceKind.SystemAudio, SpeakerId = 1, Text = "b" });
        store.Add(new TranscriptSegment { StartMs = 10000, Source = AudioSourceKind.Microphone, SpeakerId = 0, Text = "c" });

        var changed = store.RenameSpeaker(AudioSourceKind.SystemAudio, 0, "大西");

        Assert.Equal(3, changed);
        var segments = store.Snapshot();
        Assert.All(segments.Where(s => s.Source == AudioSourceKind.SystemAudio && s.SpeakerId == 0),
            s => Assert.Equal("大西", s.SpeakerName));
        Assert.Null(segments.Single(s => s.Text == "b").SpeakerName);
        Assert.Null(segments.Single(s => s.Text == "c").SpeakerName);
    }

    [Fact]
    public void ANewSegmentInheritsAnAlreadyAssignedSpeakerName()
    {
        var store = new TranscriptStore();
        store.Add(new TranscriptSegment { StartMs = 0, Source = AudioSourceKind.SystemAudio, SpeakerId = 1, Text = "x" });
        store.RenameSpeaker(AudioSourceKind.SystemAudio, 1, "橋本");

        store.Add(new TranscriptSegment { StartMs = 1000, Source = AudioSourceKind.SystemAudio, SpeakerId = 1, Text = "y" });

        Assert.Equal("橋本", store.Snapshot().Single(s => s.Text == "y").SpeakerName);
    }

    [Fact]
    public void EditingTextMarksTheSegmentAsEdited()
    {
        var store = new TranscriptStore();
        var segment = new TranscriptSegment { StartMs = 0, Text = "誤変換です" };
        store.Add(segment);

        Assert.True(store.EditText(segment.Id, "修正しました"));

        var stored = store.Snapshot().Single();
        Assert.Equal("修正しました", stored.Text);
        Assert.True(stored.IsEdited);
    }

    [Fact]
    public void SourceLabelUsesTheCaptureStreamNotAGuess()
    {
        Assert.Equal("マイク", new TranscriptSegment { Source = AudioSourceKind.Microphone }.SourceLabel());
        Assert.Equal("PC音声 / Speaker 3", new TranscriptSegment { Source = AudioSourceKind.SystemAudio, SpeakerId = 2 }.SourceLabel());
        Assert.Equal("PC音声 / 田中", new TranscriptSegment { Source = AudioSourceKind.SystemAudio, SpeakerId = 2, SpeakerName = "田中" }.SourceLabel());
    }
}
