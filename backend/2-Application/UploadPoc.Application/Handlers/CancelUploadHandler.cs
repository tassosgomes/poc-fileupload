using Microsoft.Extensions.Logging;
using UploadPoc.Application.Commands;
using UploadPoc.Domain.Enums;
using UploadPoc.Domain.Interfaces;

namespace UploadPoc.Application.Handlers;

public sealed class CancelUploadHandler
{
    private readonly IFileUploadRepository _repository;
    private readonly IStorageService? _storageService;
    private readonly ILogger<CancelUploadHandler> _logger;

    public CancelUploadHandler(
        IFileUploadRepository repository,
        ILogger<CancelUploadHandler> logger,
        IStorageService? storageService = null)
    {
        _repository = repository;
        _logger = logger;
        _storageService = storageService;
    }

    public async Task HandleAsync(CancelUploadCommand command, CancellationToken cancellationToken)
    {
        var upload = await _repository.GetByIdAsync(command.UploadId, cancellationToken)
            ?? throw new KeyNotFoundException($"Upload {command.UploadId} not found.");

        if (upload.Status != UploadStatus.Pending)
        {
            throw new InvalidOperationException($"Upload {upload.Id} is in {upload.Status} status and cannot be cancelled. Only Pending uploads can be cancelled.");
        }

        upload.MarkCancelled();

        if (upload.UploadScenario.Equals("MINIO", StringComparison.OrdinalIgnoreCase))
        {
            // TODO(tasks 7/8): when concrete MinIO storage service is available, call AbortMultipartUpload for pending multipart uploads.
        }

        if (!string.IsNullOrWhiteSpace(upload.StorageKey))
        {
            if (_storageService is null)
            {
                _logger.LogWarning(
                    "No storage service is registered. Storage cleanup skipped for upload {UploadId}.",
                    upload.Id);
            }
            else
            {
                await _storageService.DeleteAsync(upload.StorageKey, cancellationToken);
            }
        }

        await _repository.UpdateAsync(upload, cancellationToken);

        _logger.LogInformation("Upload cancelled: {UploadId}", upload.Id);
    }
}
