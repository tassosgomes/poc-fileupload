using UploadPoc.Domain.Entities;

namespace UploadPoc.Domain.Interfaces;

public interface IFileUploadRepository
{
    Task<FileUpload> AddAsync(FileUpload upload, CancellationToken cancellationToken);

    Task<FileUpload?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<FileUpload>> GetAllAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<FileUpload>> GetPendingOlderThanAsync(TimeSpan age, CancellationToken cancellationToken);

    Task UpdateAsync(FileUpload upload, CancellationToken cancellationToken);
}
