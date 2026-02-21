using FluentValidation;
using Microsoft.Extensions.Logging;
using UploadPoc.Application.Commands;
using UploadPoc.Application.Dtos;
using UploadPoc.Domain.Entities;
using UploadPoc.Domain.Interfaces;

namespace UploadPoc.Application.Handlers;

public sealed class InitiateMinioUploadHandler
{
    private const long PartSizeBytes = 104857600;

    private readonly IFileUploadRepository _repository;
    private readonly IStorageService _storageService;
    private readonly IValidator<RegisterUploadCommand> _validator;
    private readonly ILogger<InitiateMinioUploadHandler> _logger;

    public InitiateMinioUploadHandler(
        IFileUploadRepository repository,
        IStorageService storageService,
        IValidator<RegisterUploadCommand> validator,
        ILogger<InitiateMinioUploadHandler> logger)
    {
        _repository = repository;
        _storageService = storageService;
        _validator = validator;
        _logger = logger;
    }

    public async Task<InitiateMinioResponse> HandleAsync(InitiateMinioUploadCommand command, CancellationToken cancellationToken)
    {
        var validationResult = await _validator.ValidateAsync(new RegisterUploadCommand(
            command.FileName,
            command.FileSizeBytes,
            command.ContentType,
            command.ExpectedSha256,
            "MINIO",
            command.CreatedBy), cancellationToken);

        if (!validationResult.IsValid)
        {
            throw new ValidationException(validationResult.Errors);
        }

        var upload = new FileUpload(
            command.FileName,
            command.FileSizeBytes,
            command.ContentType,
            command.ExpectedSha256,
            "MINIO",
            command.CreatedBy);

        var storageKey = $"uploads/{upload.Id}/{upload.FileName}";
        upload.SetStorageKey(storageKey);

        var totalParts = (int)Math.Ceiling((double)upload.FileSizeBytes / PartSizeBytes);
        var minioUploadId = await _storageService.InitiateMultipartUploadAsync(
            _storageService.BucketName,
            storageKey,
            cancellationToken);

        upload.SetMinioUploadId(minioUploadId);

        await _repository.AddAsync(upload, cancellationToken);

        var presignedUrls = await _storageService.GeneratePresignedUrlsAsync(
            _storageService.BucketName,
            storageKey,
            minioUploadId,
            totalParts,
            cancellationToken);

        _logger.LogInformation(
            "MinIO upload initiated: {UploadId} {StorageKey} {TotalParts}",
            upload.Id,
            storageKey,
            totalParts);

        return new InitiateMinioResponse(upload.Id, storageKey, presignedUrls, PartSizeBytes, totalParts);
    }
}
