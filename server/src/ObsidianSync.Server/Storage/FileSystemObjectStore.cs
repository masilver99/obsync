using System.Security.Cryptography;

namespace ObsidianSync.Server.Storage;

public sealed class FileSystemObjectStore : IObjectStore
{
    private readonly string _root;
    private readonly string _temporaryRoot;

    public FileSystemObjectStore(string root)
    {
        _root = Path.GetFullPath(root);
        _temporaryRoot = Path.Combine(_root, ".tmp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temporaryRoot);
    }

    public async Task<ObjectWriteResult> PutAsync(Stream content, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(_temporaryRoot, $"{Guid.NewGuid():N}.tmp");
        long size = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            await using (var temporary = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await temporary.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    size += read;
                }

                await temporary.FlushAsync(cancellationToken);
            }

            var objectHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            var objectPath = GetObjectPath(objectHash);
            Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);

            if (File.Exists(objectPath))
            {
                File.Delete(temporaryPath);
            }
            else
            {
                File.Move(temporaryPath, objectPath);
            }

            return new ObjectWriteResult(objectHash, size);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string hash, CancellationToken cancellationToken)
    {
        var objectPath = GetObjectPath(hash);
        if (!File.Exists(objectPath))
        {
            throw new FileNotFoundException("The requested content object was not found.", objectPath);
        }

        Stream stream = new FileStream(objectPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken)
    {
        return Task.FromResult(File.Exists(GetObjectPath(hash)));
    }

    public Task<ObjectStoreUsage> GetUsageAsync(CancellationToken cancellationToken)
    {
        long objectCount = 0;
        long byteCount = 0;
        foreach (var filePath in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(_root, filePath);
            var firstSegment = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (string.Equals(firstSegment, ".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileInfo = new FileInfo(filePath);
            objectCount++;
            byteCount += fileInfo.Length;
        }

        return Task.FromResult(new ObjectStoreUsage(objectCount, byteCount));
    }

    public async Task<bool> CheckWritableAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, $".health-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(path, "health", cancellationToken);
            File.Delete(path);
            return true;
        }
        catch
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return false;
        }
    }

    private string GetObjectPath(string hash)
    {
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Object hashes must be 64 hexadecimal characters.", nameof(hash));
        }

        var normalized = hash.ToLowerInvariant();
        return Path.Combine(_root, normalized[..2], normalized[2..]);
    }
}
