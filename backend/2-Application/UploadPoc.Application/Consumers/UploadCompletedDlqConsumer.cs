using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UploadPoc.Domain.Entities;
using UploadPoc.Domain.Enums;
using UploadPoc.Domain.Interfaces;

namespace UploadPoc.Application.Consumers;

public sealed class UploadCompletedDlqConsumer
{
    private readonly IFileUploadRepository _repository;
    private readonly IStorageService _tusStorageService;
    private readonly IStorageService _minioStorageService;
    private readonly ILogger<UploadCompletedDlqConsumer> _logger;

    public UploadCompletedDlqConsumer(
        IFileUploadRepository repository,
        [FromKeyedServices("tus-disk")] IStorageService tusStorageService,
        [FromKeyedServices("minio")] IStorageService minioStorageService,
        ILogger<UploadCompletedDlqConsumer> logger)
    {
        _repository = repository;
        _tusStorageService = tusStorageService;
        _minioStorageService = minioStorageService;
        _logger = logger;
    }

    public async Task ProcessAsync(ReadOnlyMemory<byte> messageBody, CancellationToken cancellationToken)
    {
        var uploadId = ResolveUploadId(messageBody.Span);
        var upload = await _repository.GetByIdAsync(uploadId, cancellationToken);
        if (upload is null)
        {
            _logger.LogWarning("DLQ message ignored because upload was not found. UploadId={UploadId}", uploadId);
            return;
        }

        if (upload.Status != UploadStatus.Pending)
        {
            _logger.LogInformation(
                "DLQ message ignored because upload is already finalized. UploadId={UploadId} Status={Status}",
                upload.Id,
                upload.Status);
            return;
        }

        if (string.IsNullOrWhiteSpace(upload.StorageKey))
        {
            await MarkAsFailedAndCleanupAsync(upload, reason: "storage_key_missing", cancellationToken);
            return;
        }

        var storageService = ResolveStorageService(upload.UploadScenario);
        var exists = await storageService.ExistsAsync(upload.StorageKey, cancellationToken);
        if (!exists)
        {
            await MarkAsFailedAndCleanupAsync(upload, reason: "storage_object_not_found", cancellationToken);
            return;
        }

        if (!await IsCompleteAsync(upload, storageService, cancellationToken))
        {
            await MarkAsFailedAndCleanupAsync(upload, reason: "storage_object_incomplete", cancellationToken);
            return;
        }

        var actualSha256 = await storageService.ComputeSha256Async(upload.StorageKey, cancellationToken);
        if (!string.Equals(actualSha256, upload.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            await MarkAsCorruptedAsync(upload, actualSha256, cancellationToken);
            return;
        }

        upload.MarkCompleted(actualSha256, upload.StorageKey);
        await _repository.UpdateAsync(upload, cancellationToken);

        _logger.LogInformation(
            "Cleanup action: {UploadId} {Action} {StorageKey} {Timestamp}",
            upload.Id,
            "dlq_reconciled_completed",
            upload.StorageKey,
            DateTime.UtcNow);
    }

    private async Task MarkAsFailedAndCleanupAsync(FileUpload upload, string reason, CancellationToken cancellationToken)
    {
        var storageService = ResolveStorageService(upload.UploadScenario);

        upload.MarkFailed($"DLQ recovery failed: {reason}");
        await _repository.UpdateAsync(upload, cancellationToken);

        if (!string.IsNullOrWhiteSpace(upload.StorageKey))
        {
            try
            {
                await storageService.DeleteAsync(upload.StorageKey, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "DLQ cleanup failed to delete storage object. UploadId={UploadId} StorageKey={StorageKey}",
                    upload.Id,
                    upload.StorageKey);
            }
        }

        if (upload.UploadScenario.Equals("MINIO", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(upload.MinioUploadId)
            && !string.IsNullOrWhiteSpace(upload.StorageKey))
        {
            try
            {
                await storageService.AbortMultipartUploadAsync(
                    storageService.BucketName,
                    upload.StorageKey,
                    upload.MinioUploadId,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "DLQ cleanup failed to abort MinIO multipart upload. UploadId={UploadId} StorageKey={StorageKey}",
                    upload.Id,
                    upload.StorageKey);
            }
        }

        _logger.LogWarning(
            "Cleanup action: {UploadId} {Action} {StorageKey} {Timestamp}",
            upload.Id,
            "dlq_marked_failed",
            upload.StorageKey,
            DateTime.UtcNow);
    }

    private async Task MarkAsCorruptedAsync(FileUpload upload, string actualSha256, CancellationToken cancellationToken)
    {
        upload.MarkCorrupted(actualSha256);
        await _repository.UpdateAsync(upload, cancellationToken);

        _logger.LogWarning(
            "Cleanup action: {UploadId} {Action} {StorageKey} {Timestamp}",
            upload.Id,
            "dlq_marked_corrupted",
            upload.StorageKey,
            DateTime.UtcNow);
    }

    private static Guid ResolveUploadId(ReadOnlySpan<byte> payload)
    {
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;

        if (!root.TryGetProperty("uploadId", out var uploadIdProperty))
        {
            throw new InvalidOperationException("DLQ payload does not contain uploadId.");
        }

        var uploadIdText = uploadIdProperty.GetString();
        if (!Guid.TryParse(uploadIdText, out var uploadId))
        {
            throw new InvalidOperationException("DLQ payload uploadId is invalid.");
        }

        return uploadId;
    }

    private static async Task<bool> IsCompleteAsync(
        FileUpload upload,
        IStorageService storageService,
        CancellationToken cancellationToken)
    {
        await using var stream = await storageService.GetObjectStreamAsync(upload.StorageKey!, cancellationToken);

        if (!stream.CanSeek)
        {
            return true;
        }

        return stream.Length == upload.FileSizeBytes;
    }

    private IStorageService ResolveStorageService(string uploadScenario)
    {
        if (uploadScenario.Equals("TUS", StringComparison.OrdinalIgnoreCase))
        {
            return _tusStorageService;
        }

        if (uploadScenario.Equals("MINIO", StringComparison.OrdinalIgnoreCase))
        {
            return _minioStorageService;
        }

        throw new InvalidOperationException($"Upload scenario '{uploadScenario}' is not supported.");
    }
}
