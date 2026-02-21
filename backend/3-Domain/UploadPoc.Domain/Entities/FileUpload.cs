using UploadPoc.Domain.Enums;

namespace UploadPoc.Domain.Entities;

public class FileUpload
{
    public Guid Id { get; private set; }

    public string FileName { get; private set; }

    public long FileSizeBytes { get; private set; }

    public string ContentType { get; private set; }

    public string ExpectedSha256 { get; private set; }

    public string? ActualSha256 { get; private set; }

    public string UploadScenario { get; private set; }

    public string? StorageKey { get; private set; }

    public string? MinioUploadId { get; private set; }

    public UploadStatus Status { get; private set; }

    public string CreatedBy { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? CompletedAt { get; private set; }

    public FileUpload(
        string fileName,
        long fileSizeBytes,
        string contentType,
        string expectedSha256,
        string uploadScenario,
        string createdBy)
    {
        FileName = ValidateRequired(fileName, nameof(fileName));

        if (fileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileSizeBytes), "File size must be greater than zero.");
        }

        FileSizeBytes = fileSizeBytes;
        ContentType = ValidateRequired(contentType, nameof(contentType));
        ExpectedSha256 = ValidateRequired(expectedSha256, nameof(expectedSha256));
        UploadScenario = ValidateUploadScenario(uploadScenario);
        CreatedBy = ValidateRequired(createdBy, nameof(createdBy));

        Id = Guid.NewGuid();
        Status = UploadStatus.Pending;
        CreatedAt = DateTime.UtcNow;
    }

    private FileUpload()
    {
        FileName = string.Empty;
        ContentType = string.Empty;
        ExpectedSha256 = string.Empty;
        UploadScenario = string.Empty;
        CreatedBy = string.Empty;
    }

    public void MarkCompleted(string actualSha256, string storageKey)
    {
        EnsurePendingStatus("complete");

        ActualSha256 = ValidateRequired(actualSha256, nameof(actualSha256));
        StorageKey = ValidateRequired(storageKey, nameof(storageKey));
        Status = UploadStatus.Completed;
        CompletedAt = DateTime.UtcNow;
    }

    public void MarkCorrupted(string actualSha256)
    {
        EnsurePendingStatus("mark as corrupted");

        ActualSha256 = ValidateRequired(actualSha256, nameof(actualSha256));
        Status = UploadStatus.Corrupted;
        CompletedAt = DateTime.UtcNow;
    }

    public void MarkCancelled()
    {
        EnsurePendingStatus("cancel");

        Status = UploadStatus.Cancelled;
        CompletedAt = DateTime.UtcNow;
    }

    public void MarkFailed(string reason)
    {
        EnsurePendingStatus("mark as failed");
        _ = ValidateRequired(reason, nameof(reason));

        Status = UploadStatus.Failed;
        CompletedAt = DateTime.UtcNow;
    }

    public void SetStorageKey(string storageKey)
    {
        StorageKey = ValidateRequired(storageKey, nameof(storageKey));
    }

    public void SetMinioUploadId(string minioUploadId)
    {
        MinioUploadId = ValidateRequired(minioUploadId, nameof(minioUploadId));
    }

    private void EnsurePendingStatus(string action)
    {
        if (Status != UploadStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot {action} upload with status {Status}.");
        }
    }

    private static string ValidateRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", parameterName);
        }

        return value.Trim();
    }

    private static string ValidateUploadScenario(string uploadScenario)
    {
        var scenario = ValidateRequired(uploadScenario, nameof(uploadScenario));

        if (!scenario.Equals("TUS", StringComparison.OrdinalIgnoreCase)
            && !scenario.Equals("MINIO", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Upload scenario must be TUS or MINIO.", nameof(uploadScenario));
        }

        return scenario.ToUpperInvariant();
    }
}
