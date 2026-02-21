namespace UploadPoc.Domain.Interfaces;

public interface IStorageService
{
    Task<string> ComputeSha256Async(string storageKey, CancellationToken cancellationToken);

    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken);
}
