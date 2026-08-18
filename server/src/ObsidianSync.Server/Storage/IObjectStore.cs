namespace ObsidianSync.Server.Storage;

public sealed record ObjectWriteResult(string Hash, long Size);
public sealed record ObjectStoreUsage(long ObjectCount, long ByteCount);

public interface IObjectStore
{
    Task<ObjectWriteResult> PutAsync(Stream content, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string hash, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken);
    Task<bool> CheckWritableAsync(CancellationToken cancellationToken);
    Task<ObjectStoreUsage> GetUsageAsync(CancellationToken cancellationToken);
}
