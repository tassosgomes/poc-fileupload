using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UploadPoc.Domain.Events;
using UploadPoc.Domain.Interfaces;

namespace UploadPoc.Application.Consumers;

public sealed class UploadCompletedConsumer : IIntegrityCheckHandler
{
    private readonly IFileUploadRepository _repository;
    private readonly IStorageService _tusStorageService;
    private readonly IStorageService _minioStorageService;
    private readonly ILogger<UploadCompletedConsumer> _logger;

    public UploadCompletedConsumer(
        IFileUploadRepository repository,
        [FromKeyedServices("tus-disk")] IStorageService tusStorageService,
        [FromKeyedServices("minio")] IStorageService minioStorageService,
        ILogger<UploadCompletedConsumer> logger)
    {
        _repository = repository;
        _tusStorageService = tusStorageService;
        _minioStorageService = minioStorageService;
        _logger = logger;
    }

    public Task HandleAsync(UploadCompletedEvent @event, CancellationToken cancellationToken)
    {
        return ProcessAsync(@event, cancellationToken);
    }

    public async Task ProcessAsync(UploadCompletedEvent uploadCompletedEvent, CancellationToken cancellationToken)
    {
        var upload = await _repository.GetByIdAsync(uploadCompletedEvent.UploadId, cancellationToken)
            ?? throw new InvalidOperationException($"Upload {uploadCompletedEvent.UploadId} not found.");

        var storageService = ResolveStorageService(uploadCompletedEvent.UploadScenario);
        var actualSha256 = await storageService.ComputeSha256Async(uploadCompletedEvent.StorageKey, cancellationToken);

        if (string.Equals(actualSha256, uploadCompletedEvent.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            upload.MarkCompleted(actualSha256, uploadCompletedEvent.StorageKey);
            _logger.LogInformation(
                "Upload integrity validation succeeded. UploadId={UploadId} Scenario={Scenario} Sha256={ActualSha256}",
                uploadCompletedEvent.UploadId,
                uploadCompletedEvent.UploadScenario,
                actualSha256);
        }
        else
        {
            upload.MarkCorrupted(actualSha256);
            _logger.LogWarning(
                "Upload integrity validation failed. UploadId={UploadId} Scenario={Scenario} ExpectedSha256={ExpectedSha256} ActualSha256={ActualSha256}",
                uploadCompletedEvent.UploadId,
                uploadCompletedEvent.UploadScenario,
                uploadCompletedEvent.ExpectedSha256,
                actualSha256);
        }

        await _repository.UpdateAsync(upload, cancellationToken);
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
