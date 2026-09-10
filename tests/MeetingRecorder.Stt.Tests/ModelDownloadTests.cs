using MeetingRecorder.Core.Stt;
using System.Net;
using System.Security.Cryptography;
using MeetingRecorder.Core.ModelManagement;
using Xunit;

namespace MeetingRecorder.Stt.Tests;

/// <summary>
/// Exercises the only outbound network path in the product, against a stub
/// handler. No real download happens here, and none is needed: what matters is
/// that a hash mismatch is rejected, that a resumed download asks for the right
/// range, and that a URL outside the catalog is refused outright.
/// </summary>
public sealed class ModelDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mr-model-" + Guid.NewGuid().ToString("N"));

    public ModelDownloadTests() => Directory.CreateDirectory(_root);

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

    private static byte[] Payload(int size = 4096)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    private static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static ModelDescriptor CatalogModelWith(string? sha256)
    {
        // Reuse a real catalog entry so the "must be in the catalog" guard passes.
        var template = ModelCatalog.RequireSpeechModel(ModelCatalog.WhisperTiny);
        return template with { Sha256 = sha256, ApproximateSizeBytes = 4096 };
    }

    [Fact]
    public async Task DownloadsVerifiesAndRecordsTheHashWhenOnePinnedHashMatches()
    {
        var payload = Payload();
        var store = new ModelStore(_root);
        var downloader = new ModelDownloader(store, handler: new StubHandler(payload));

        var descriptor = CatalogModelWith(Sha256Of(payload));
        var result = await downloader.DownloadAsync(descriptor);

        Assert.True(result.VerifiedAgainstPinnedHash);
        Assert.Equal(Sha256Of(payload), result.Sha256);
        Assert.True(File.Exists(result.Path));

        var entry = store.FindManifestEntry(descriptor.Id);
        Assert.NotNull(entry);
        Assert.True(entry!.VerifiedAgainstPinnedHash);
        Assert.Equal(payload.Length, entry.SizeBytes);
    }

    [Fact]
    public async Task RejectsAndDeletesAFileWhoseHashDoesNotMatch()
    {
        var store = new ModelStore(_root);
        var downloader = new ModelDownloader(store, handler: new StubHandler(Payload()));

        var descriptor = CatalogModelWith(new string('a', 64));

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(descriptor));

        Assert.False(File.Exists(store.PathFor(descriptor)));
        Assert.False(File.Exists(store.PathFor(descriptor) + ".part"));
    }

    [Fact]
    public async Task RecordsButDoesNotClaimVerificationWhenNoHashIsPinned()
    {
        var payload = Payload();
        var store = new ModelStore(_root);
        var downloader = new ModelDownloader(store, handler: new StubHandler(payload));

        var result = await downloader.DownloadAsync(CatalogModelWith(null));

        Assert.False(result.VerifiedAgainstPinnedHash);
        Assert.Equal(Sha256Of(payload), result.Sha256);
    }

    [Fact]
    public async Task ResumesFromAPartialFileInsteadOfStartingOver()
    {
        var payload = Payload();
        var store = new ModelStore(_root);
        var descriptor = CatalogModelWith(Sha256Of(payload));

        // Pretend an earlier attempt got half way.
        var partial = store.PathFor(descriptor) + ".part";
        await File.WriteAllBytesAsync(partial, payload[..2048]);

        var handler = new StubHandler(payload);
        var downloader = new ModelDownloader(store, handler: handler);
        var result = await downloader.DownloadAsync(descriptor);

        Assert.Equal(2048, handler.LastRangeFrom);
        Assert.True(result.VerifiedAgainstPinnedHash);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.Path));
    }

    [Fact]
    public async Task RefusesAnyModelThatIsNotInTheCatalog()
    {
        var store = new ModelStore(_root);
        var downloader = new ModelDownloader(store, handler: new StubHandler(Payload()));

        var rogue = ModelCatalog.RequireSpeechModel(ModelCatalog.WhisperTiny) with
        {
            Id = "attacker-supplied",
            Url = "https://example.invalid/evil.bin",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => downloader.DownloadAsync(rogue));
    }

    [Fact]
    public async Task RefusesPlainHttp()
    {
        var store = new ModelStore(_root);
        var downloader = new ModelDownloader(store, handler: new StubHandler(Payload()));

        var insecure = ModelCatalog.RequireSpeechModel(ModelCatalog.WhisperTiny) with
        {
            Url = "http://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny-q5_1.bin",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => downloader.DownloadAsync(insecure));
    }

    [Fact]
    public async Task ReportsGenuineProgressThatReachesCompletion()
    {
        var payload = Payload(64 * 1024);
        var store = new ModelStore(_root);
        var downloader = new ModelDownloader(store, handler: new StubHandler(payload));

        var reports = new List<ModelDownloadProgress>();
        await downloader.DownloadAsync(
            CatalogModelWith(Sha256Of(payload)),
            new Progress<ModelDownloadProgress>(p =>
            {
                lock (reports)
                {
                    reports.Add(p);
                }
            }));

        for (var i = 0; i < 50 && !reports.Any(r => r.Phase == ModelDownloadPhase.Completed); i++)
        {
            await Task.Delay(10);
        }

        Assert.Contains(reports, r => r.Phase == ModelDownloadPhase.Completed);
        Assert.All(reports, r => Assert.True(r.BytesReceived >= 0));
    }

    [Fact]
    public void PartiallyDownloadedFilesAreNotTreatedAsPresent()
    {
        var store = new ModelStore(_root);
        var descriptor = ModelCatalog.RequireSpeechModel(ModelCatalog.WhisperBase);

        File.WriteAllBytes(store.PathFor(descriptor), new byte[1024]);
        Assert.False(store.IsPresent(descriptor));
    }

    [Fact]
    public void ComputedHashesMatchTheReferenceImplementation()
    {
        var payload = Payload();
        var path = Path.Combine(_root, "hash.bin");
        File.WriteAllBytes(path, payload);

        Assert.Equal(Sha256Of(payload), ModelStore.ComputeSha256(path));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;

        public StubHandler(byte[] payload) => _payload = payload;

        public long? LastRangeFrom { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            LastRangeFrom = from;

            var body = from is { } offset ? _payload[(int)offset..] : _payload;
            var response = new HttpResponseMessage(from is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(body),
            };

            return Task.FromResult(response);
        }
    }
}

public class SpeechRecognizerContractTests
{
    [Fact]
    public void CreatingARecognizerForAMissingModelFailsClearlyRatherThanSilently()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-model-" + Guid.NewGuid().ToString("N") + ".bin");

        var exception = Assert.Throws<FileNotFoundException>(
            () => new WhisperSpeechRecognizer(missing, "test", SpeechRecognitionOptions.Offline(2, "ja")));

        Assert.Contains("モデル", exception.Message);
    }

    [Fact]
    public void TheFactoryIsTheOnlyWayTheAppObtainsARecognizer()
    {
        var factory = new WhisperSpeechRecognizerFactory();
        var missing = Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid().ToString("N") + ".bin");

        Assert.Throws<FileNotFoundException>(() => factory.Create(missing, SpeechRecognitionOptions.Offline(2, "ja")));
    }
}
