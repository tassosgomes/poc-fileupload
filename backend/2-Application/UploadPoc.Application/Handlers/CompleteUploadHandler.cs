using Microsoft.Extensions.Logging;
using UploadPoc.Application.Commands;
using UploadPoc.Domain.Enums;
using UploadPoc.Domain.Events;
using UploadPoc.Domain.Interfaces;

namespace UploadPoc.Application.Handlers;

public sealed class CompleteUploadHandler
{
    private readonly IFileUploadRepository _repository;
    private readonly IStorageService _storageService;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<CompleteUploadHandler> _logger;

    public CompleteUploadHandler(
        IFileUploadRepository repository,
        IStorageService storageService,
        IEventPublisher eventPublisher,
        ILogger<CompleteUploadHandler> logger)
    {
        _repository = repository;
        _storageService = storageService;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task HandleAsync(CompleteUploadCommand command, CancellationToken cancellationToken)
    {
        if (command.Parts.Count == 0)
        {
            throw new ArgumentException("At least one completed part is required.", nameof(command.Parts));
        }

        var upload = await _repository.GetByIdAsync(command.UploadId, cancellationToken)
            ?? throw new KeyNotFoundException($"Upload {command.UploadId} not found.");

        if (upload.Status != UploadStatus.Pending)
        {
            throw new InvalidOperationException($"Upload {upload.Id} must be in Pending status to complete.");
        }

        if (!upload.UploadScenario.Equals("MINIO", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Upload {upload.Id} is not a MinIO upload.");
        }

        if (string.IsNullOrWhiteSpace(upload.StorageKey))
        {
            throw new InvalidOperationException($"Upload {upload.Id} does not have a storage key.");
        }

        if (string.IsNullOrWhiteSpace(upload.MinioUploadId))
        {
            throw new InvalidOperationException($"Upload {upload.Id} does not have a MinIO multipart upload id.");
        }

        var parts = command.Parts
            .OrderBy(part => part.PartNumber)
            .Select(part => new StoragePartInfo(part.PartNumber, part.ETag))
            .ToList();

        await _storageService.CompleteMultipartUploadAsync(
            _storageService.BucketName,
            upload.StorageKey,
            upload.MinioUploadId,
            parts,
            cancellationToken);

        // Upload stays Pending until the integrity consumer validates checksum and sets final status.
        await _repository.UpdateAsync(upload, cancellationToken);

        await _eventPublisher.PublishUploadCompletedAsync(new UploadCompletedEvent(
            upload.Id,
            upload.StorageKey,
            upload.ExpectedSha256,
            upload.UploadScenario,
            DateTime.UtcNow), cancellationToken);

        _logger.LogInformation("MinIO multipart upload completed and queued for integrity validation: {UploadId}", upload.Id);
    }
}
