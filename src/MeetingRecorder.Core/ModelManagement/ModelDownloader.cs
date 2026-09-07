using System.Net;
using System.Net.Http.Headers;
using MeetingRecorder.Core.Diagnostics;

namespace MeetingRecorder.Core.ModelManagement;

/// <param name="BytesReceived">Bytes written so far, including a resumed prefix.</param>
/// <param name="TotalBytes">Total size when the server reports it.</param>
/// <param name="BytesPerSecond">Current throughput.</param>
/// <param name="Phase">Which stage the download is in, for the UI.</param>
public readonly record struct ModelDownloadProgress(
    long BytesReceived,
    long? TotalBytes,
    double BytesPerSecond,
    ModelDownloadPhase Phase)
{
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp(BytesReceived / (double)TotalBytes.Value, 0, 1) : null;

    public TimeSpan? Remaining => TotalBytes is > 0 && BytesPerSecond > 1
        ? TimeSpan.FromSeconds((TotalBytes.Value - BytesReceived) / BytesPerSecond)
        : null;
}

public enum ModelDownloadPhase
{
    Connecting,
    Downloading,
    Verifying,
    Completed,
}

public sealed class ModelDownloadResult
{
    public required string Path { get; init; }

    public required string Sha256 { get; init; }

    /// <summary>True when the catalog pinned a hash and the file matched it.</summary>
    public required bool VerifiedAgainstPinnedHash { get; init; }

    public required long SizeBytes { get; init; }
}

/// <summary>
/// The only component in the application that opens an outbound connection.
/// </summary>
/// <remarks>
/// <para>
/// It downloads AI model files and nothing else. It refuses any URL that is not
/// HTTPS and not in <see cref="ModelCatalog"/>, it never transmits anything
/// about the user or their meetings, and it has no analytics of any kind.
/// </para>
/// <para>
/// Integrity: when the catalog pins a SHA-256 the file is rejected unless it
/// matches. When no hash is pinned the downloader still computes one, records it
/// in the local manifest, and reports honestly that the file could only be
/// recorded rather than verified - it never claims a verification that did not
/// happen.
/// </para>
/// </remarks>
public sealed class ModelDownloader
{
    private readonly HttpClient _http;
    private readonly ModelStore _store;
    private readonly ILogger _logger;

    public ModelDownloader(ModelStore store, ILogger? logger = null, HttpMessageHandler? handler = null)
    {
        _store = store;
        _logger = logger ?? NullLogger.Instance;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = TimeSpan.FromMinutes(30);
        // A neutral, honest user agent. No machine identifiers, no user id.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MeetingRecorder/0.1 (+local model download)");
    }

    public async Task<ModelDownloadResult> DownloadAsync(
        ModelDescriptor descriptor,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (ModelCatalog.Find(descriptor.Id) is null)
        {
            throw new InvalidOperationException($"Model '{descriptor.Id}' is not in the catalog; refusing to download.");
        }

        var uri = new Uri(descriptor.Url);
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Model downloads must use HTTPS.");
        }

        var destination = _store.PathFor(descriptor);
        var partial = destination + ".part";
        Directory.CreateDirectory(_store.Directory);

        progress?.Report(new ModelDownloadProgress(0, descriptor.ApproximateSizeBytes, 0, ModelDownloadPhase.Connecting));

        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (existing > 0)
        {
            // Resume: a 1 GB model over a corporate VPN is a long download and
            // restarting from zero after a disconnect is a bad experience.
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            existing = 0;
            File.Delete(partial);
        }

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength is { } length ? existing + length : (long?)descriptor.ApproximateSizeBytes;

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(partial, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 256))
        {
            var buffer = new byte[256 * 1024];
            var received = existing;
            var started = DateTime.UtcNow;
            var lastReport = DateTime.UtcNow;

            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;

                var now = DateTime.UtcNow;
                if ((now - lastReport).TotalMilliseconds >= 200)
                {
                    var elapsed = Math.Max(0.001, (now - started).TotalSeconds);
                    progress?.Report(new ModelDownloadProgress(
                        received,
                        total,
                        (received - existing) / elapsed,
                        ModelDownloadPhase.Downloading));
                    lastReport = now;
                }
            }

            progress?.Report(new ModelDownloadProgress(received, total, 0, ModelDownloadPhase.Downloading));
        }

        progress?.Report(new ModelDownloadProgress(total ?? 0, total, 0, ModelDownloadPhase.Verifying));

        var sha = ModelStore.ComputeSha256(partial, null, cancellationToken);
        var pinned = descriptor.Sha256;
        var verified = false;

        if (!string.IsNullOrWhiteSpace(pinned))
        {
            if (!string.Equals(sha, pinned, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException(
                    $"ダウンロードしたモデル '{descriptor.DisplayName}' のSHA-256が一致しません。ファイルを削除しました。" +
                    $"(expected {pinned}, actual {sha})");
            }

            verified = true;
        }
        else
        {
            _logger.Warn(
                nameof(ModelDownloader),
                $"No pinned SHA-256 for '{descriptor.Id}'; recorded {sha} in the local manifest instead.");
        }

        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        File.Move(partial, destination);
        var size = new FileInfo(destination).Length;

        _store.RecordDownload(new ModelManifestEntry
        {
            Id = descriptor.Id,
            FileName = descriptor.FileName,
            SizeBytes = size,
            Sha256 = sha,
            VerifiedAgainstPinnedHash = verified,
            SourceUrl = descriptor.Url,
        });

        progress?.Report(new ModelDownloadProgress(size, size, 0, ModelDownloadPhase.Completed));
        _logger.Info(nameof(ModelDownloader), $"Model '{descriptor.Id}' downloaded ({size:N0} bytes, sha256={sha}, pinned={verified}).");

        return new ModelDownloadResult
        {
            Path = destination,
            Sha256 = sha,
            VerifiedAgainstPinnedHash = verified,
            SizeBytes = size,
        };
    }
}
