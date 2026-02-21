using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UploadPoc.Application.Dtos;
using UploadPoc.Application.Queries;
using UploadPoc.Domain.Enums;
using UploadPoc.Domain.Interfaces;

namespace UploadPoc.Application.Handlers;

public sealed class GetDownloadUrlHandler
{
    private readonly IFileUploadRepository _repository;
    private readonly IStorageService _minioStorageService;
    private readonly string _tusStoragePath;

    public GetDownloadUrlHandler(
        IFileUploadRepository repository,
        [FromKeyedServices("minio")] IStorageService minioStorageService,
        IConfiguration configuration)
    {
        _repository = repository;
        _minioStorageService = minioStorageService;
        _tusStoragePath = configuration["TusStorage:Path"] ?? "/app/uploads";
    }

    public async Task<DownloadResult> HandleAsync(GetDownloadUrlQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var upload = await _repository.GetByIdAsync(query.UploadId, cancellationToken)
            ?? throw new KeyNotFoundException($"Upload {query.UploadId} not found.");

        if (upload.Status != UploadStatus.Completed)
        {
            throw new InvalidOperationException($"Download is only available for completed uploads. Current status: {upload.Status}.");
        }

        if (string.IsNullOrWhiteSpace(upload.StorageKey))
        {
            throw new InvalidOperationException($"Upload {upload.Id} does not have a storage key.");
        }

        if (upload.UploadScenario.Equals("TUS", StringComparison.OrdinalIgnoreCase))
        {
            var filePath = Path.Combine(_tusStoragePath, upload.StorageKey);
            if (!File.Exists(filePath))
            {
                throw new KeyNotFoundException($"Upload file for {upload.Id} was not found on disk.");
            }

            return new DownloadResult("TUS", upload.FileName, upload.ContentType, filePath, null);
        }

        if (upload.UploadScenario.Equals("MINIO", StringComparison.OrdinalIgnoreCase))
        {
            var presignedUrl = _minioStorageService.GeneratePresignedDownloadUrl(upload.StorageKey, upload.FileName);
            return new DownloadResult("MINIO", upload.FileName, upload.ContentType, null, presignedUrl);
        }

        throw new InvalidOperationException($"Upload scenario '{upload.UploadScenario}' is not supported.");
    }
}
