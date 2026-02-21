using FluentValidation;
using Microsoft.Extensions.Logging;
using UploadPoc.Application.Commands;
using UploadPoc.Application.Dtos;
using UploadPoc.Domain.Entities;
using UploadPoc.Domain.Interfaces;

namespace UploadPoc.Application.Handlers;

public sealed class RegisterUploadHandler
{
    private readonly IFileUploadRepository _repository;
    private readonly IValidator<RegisterUploadCommand> _validator;
    private readonly ILogger<RegisterUploadHandler> _logger;

    public RegisterUploadHandler(
        IFileUploadRepository repository,
        IValidator<RegisterUploadCommand> validator,
        ILogger<RegisterUploadHandler> logger)
    {
        _repository = repository;
        _validator = validator;
        _logger = logger;
    }

    public async Task<UploadDto> HandleAsync(RegisterUploadCommand command, CancellationToken cancellationToken)
    {
        var validationResult = await _validator.ValidateAsync(command, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new ValidationException(validationResult.Errors);
        }

        var upload = new FileUpload(
            command.FileName,
            command.FileSizeBytes,
            command.ContentType,
            command.ExpectedSha256,
            command.UploadScenario,
            command.CreatedBy);

        upload.SetStorageKey($"uploads/{upload.Id}/{upload.FileName}");

        await _repository.AddAsync(upload, cancellationToken);

        _logger.LogInformation(
            "Upload registered: {UploadId} {FileName} {Scenario} {FileSize}",
            upload.Id,
            upload.FileName,
            upload.UploadScenario,
            upload.FileSizeBytes);

        return MapToDto(upload);
    }

    private static UploadDto MapToDto(FileUpload upload)
    {
        return new UploadDto(
            upload.Id,
            upload.FileName,
            upload.FileSizeBytes,
            upload.ContentType,
            upload.ExpectedSha256,
            upload.ActualSha256,
            upload.UploadScenario,
            upload.StorageKey,
            upload.Status.ToString().ToUpperInvariant(),
            upload.CreatedBy,
            upload.CreatedAt,
            upload.CompletedAt);
    }
}
