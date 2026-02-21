using Microsoft.Extensions.Configuration;
using UploadPoc.Domain.Interfaces;

namespace UploadPoc.Infra.Storage;

public sealed class TusDiskStorageService : IStorageService
{
    private readonly string _basePath;
    private readonly IChecksumService _checksumService;

    public TusDiskStorageService(IConfiguration configuration, IChecksumService checksumService)
    {
        _basePath = configuration["TusStorage:Path"] ?? "/app/uploads";
        _checksumService = checksumService;

        Directory.CreateDirectory(_basePath);
    }

    public string BucketName => string.Empty;

    public Task<string> InitiateMultipartUploadAsync(string bucketName, string key, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Multipart upload is not supported for TUS disk storage.");
    }

    public Task<IReadOnlyList<string>> GeneratePresignedUrlsAsync(
        string bucketName,
        string key,
        string uploadId,
        int totalParts,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Presigned URLs are not supported for TUS disk storage.");
    }

    public Task CompleteMultipartUploadAsync(
        string bucketName,
        string key,
        string uploadId,
        IReadOnlyList<StoragePartInfo> parts,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Multipart upload is not supported for TUS disk storage.");
    }

    public Task AbortMultipartUploadAsync(string bucketName, string key, string uploadId, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Multipart upload is not supported for TUS disk storage.");
    }

    public async Task<string> ComputeSha256Async(string storageKey, CancellationToken cancellationToken)
    {
        await using var stream = await GetObjectStreamAsync(storageKey, cancellationToken);
        return await _checksumService.ComputeSha256Async(stream, cancellationToken);
    }

    public Task<Stream> GetObjectStreamAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var filePath = GetFilePath(storageKey);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"TUS file '{storageKey}' was not found.", filePath);
        }

        Stream stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var filePath = GetFilePath(storageKey);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        var metadataFiles = Directory.GetFiles(_basePath, $"{storageKey}.*", SearchOption.TopDirectoryOnly);
        foreach (var metadataFile in metadataFiles)
        {
            if (File.Exists(metadataFile))
            {
                File.Delete(metadataFile);
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var filePath = GetFilePath(storageKey);
        return Task.FromResult(File.Exists(filePath));
    }

    private string GetFilePath(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new ArgumentException("Storage key cannot be null or whitespace.", nameof(storageKey));
        }

        if (storageKey.Contains(Path.DirectorySeparatorChar)
            || storageKey.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException("Invalid storage key path.");
        }

        var baseFullPath = Path.GetFullPath(_basePath);
        var combinedPath = Path.GetFullPath(Path.Combine(baseFullPath, storageKey));
        var basePathWithSeparator = baseFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? baseFullPath
            : baseFullPath + Path.DirectorySeparatorChar;

        if (!combinedPath.StartsWith(basePathWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid storage key path.");
        }

        return combinedPath;
    }
}
