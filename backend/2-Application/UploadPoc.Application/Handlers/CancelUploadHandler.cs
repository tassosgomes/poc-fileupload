using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UploadPoc.Application.Commands;
using UploadPoc.Domain.Enums;
using UploadPoc.Domain.Interfaces;

namespace UploadPoc.Application.Handlers;

public sealed class CancelUploadHandler
{
    private readonly IFileUploadRepository _repository;
    private readonly IStorageService? _storageService;
    private readonly IKeyedServiceProvider? _keyedServiceProvider;
    private readonly ILogger<CancelUploadHandler> _logger;

    public CancelUploadHandler(
        IFileUploadRepository repository,
        ILogger<CancelUploadHandler> logger,
        IStorageService? storageService = null,
        IKeyedServiceProvider? keyedServiceProvider = null)
    {
        _repository = repository;
        _logger = logger;
        _storageService = storageService;
        _keyedServiceProvider = keyedServiceProvider;
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
            if (_storageService is null)
            {
                _logger.LogWarning(
                    "No storage service is registered. Storage cleanup skipped for upload {UploadId}.",
                    upload.Id);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(upload.StorageKey)
                    && !string.IsNullOrWhiteSpace(upload.MinioUploadId))
                {
                    await _storageService.AbortMultipartUploadAsync(
                        _storageService.BucketName,
                        upload.StorageKey,
                        upload.MinioUploadId,
                        cancellationToken);
                }
                else
                {
                    _logger.LogWarning(
                        "Cannot abort MinIO multipart upload {UploadId}. StorageKey or MinioUploadId is missing.",
                        upload.Id);
                }
            }
        }
        else if (upload.UploadScenario.Equals("TUS", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(upload.StorageKey))
            {
                _logger.LogWarning(
                    "Cannot delete TUS partial upload {UploadId}. StorageKey is missing.",
                    upload.Id);
            }
            else
            {
                var tusStorageService = _keyedServiceProvider?
                    .GetKeyedService(typeof(IStorageService), "tus-disk") as IStorageService;

                if (tusStorageService is null)
                {
                    _logger.LogWarning(
                        "No keyed TUS storage service is registered. Storage cleanup skipped for upload {UploadId}.",
                        upload.Id);
                }
                else
                {
                    await tusStorageService.DeleteAsync(upload.StorageKey, cancellationToken);
                }
            }
        }

        await _repository.UpdateAsync(upload, cancellationToken);

        _logger.LogInformation("Upload cancelled: {UploadId}", upload.Id);
    }
}
