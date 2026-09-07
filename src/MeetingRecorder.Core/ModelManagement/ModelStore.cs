using System.Security.Cryptography;
using MeetingRecorder.Core.Persistence;

namespace MeetingRecorder.Core.ModelManagement;

/// <summary>Local record of what was downloaded and what its hash turned out to be.</summary>
public sealed class ModelManifestEntry
{
    public string Id { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    /// <summary>True when the hash matched a value pinned in the catalog.</summary>
    public bool VerifiedAgainstPinnedHash { get; set; }

    public DateTimeOffset DownloadedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string SourceUrl { get; set; } = string.Empty;
}

public sealed class ModelManifest
{
    public List<ModelManifestEntry> Entries { get; set; } = new();
}

/// <summary>Owns the model directory: where files live, whether they are present and intact.</summary>
public sealed class ModelStore
{
    private readonly object _sync = new();

    public ModelStore(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public string Directory { get; }

    public string ManifestPath => Path.Combine(Directory, "models.json");

    public string PathFor(ModelDescriptor descriptor) => Path.Combine(Directory, descriptor.FileName);

    public bool IsPresent(ModelDescriptor descriptor)
    {
        var path = PathFor(descriptor);
        if (!File.Exists(path))
        {
            return false;
        }

        // A partially downloaded file is worse than a missing one, because the
        // engine would fail with an opaque native error. Reject anything that is
        // obviously short.
        var length = new FileInfo(path).Length;
        return length > descriptor.ApproximateSizeBytes * 0.5;
    }

    public ModelManifest ReadManifest()
    {
        lock (_sync)
        {
            return JsonStore.Read<ModelManifest>(ManifestPath) ?? new ModelManifest();
        }
    }

    public void RecordDownload(ModelManifestEntry entry)
    {
        lock (_sync)
        {
            var manifest = JsonStore.Read<ModelManifest>(ManifestPath) ?? new ModelManifest();
            manifest.Entries.RemoveAll(e => string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            manifest.Entries.Add(entry);
            JsonStore.WriteAtomic(ManifestPath, manifest);
        }
    }

    public ModelManifestEntry? FindManifestEntry(string id)
        => ReadManifest().Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string ComputeSha256(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        using var sha = SHA256.Create();

        var buffer = new byte[1024 * 1024];
        long total = 0;
        var length = stream.Length;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, null, 0);
            total += read;
            if (length > 0)
            {
                progress?.Report(total / (double)length);
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>
    /// Re-checks a model against the hash recorded when it was downloaded.
    /// Detects a corrupted or tampered file even when the catalog has no pinned hash.
    /// </summary>
    public bool VerifyAgainstManifest(ModelDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        var entry = FindManifestEntry(descriptor.Id);
        if (entry is null || !IsPresent(descriptor))
        {
            return false;
        }

        var expected = descriptor.Sha256 ?? entry.Sha256;
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        var actual = ComputeSha256(PathFor(descriptor), null, cancellationToken);
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
