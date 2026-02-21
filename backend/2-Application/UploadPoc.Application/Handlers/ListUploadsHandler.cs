using UploadPoc.Application.Dtos;
using UploadPoc.Application.Queries;
using UploadPoc.Domain.Entities;
using UploadPoc.Domain.Interfaces;

namespace UploadPoc.Application.Handlers;

public sealed class ListUploadsHandler
{
    private readonly IFileUploadRepository _repository;

    public ListUploadsHandler(IFileUploadRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<UploadDto>> HandleAsync(ListUploadsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var uploads = await _repository.GetAllAsync(cancellationToken);

        return uploads
            .OrderByDescending(upload => upload.CreatedAt)
            .Select(MapToDto)
            .ToArray();
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
