namespace UploadPoc.Domain.Interfaces;

public interface IStorageService
{
    string BucketName { get; }

    Task<string> InitiateMultipartUploadAsync(string bucketName, string key, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GeneratePresignedUrlsAsync(
        string bucketName,
        string key,
        string uploadId,
        int totalParts,
        CancellationToken cancellationToken);

    string GeneratePresignedDownloadUrl(string key, string? fileName = null);

    Task CompleteMultipartUploadAsync(
        string bucketName,
        string key,
        string uploadId,
        IReadOnlyList<StoragePartInfo> parts,
        CancellationToken cancellationToken);

    Task AbortMultipartUploadAsync(string bucketName, string key, string uploadId, CancellationToken cancellationToken);

    Task<string> ComputeSha256Async(string storageKey, CancellationToken cancellationToken);

    Task<Stream> GetObjectStreamAsync(string storageKey, CancellationToken cancellationToken);

    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken);
}

public sealed record StoragePartInfo(int PartNumber, string ETag);
