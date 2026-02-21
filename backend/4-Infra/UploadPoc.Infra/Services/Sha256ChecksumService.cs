using System.Security.Cryptography;
using UploadPoc.Domain.Interfaces;

namespace UploadPoc.Infra.Services;

public sealed class Sha256ChecksumService : IChecksumService
{
    public async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
