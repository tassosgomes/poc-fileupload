namespace UploadPoc.Domain.Interfaces;

public interface IChecksumService
{
    Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken);
}
