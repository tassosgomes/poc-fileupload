namespace UploadPoc.Application.Dtos;

public sealed record InitiateMinioResponse(
    Guid UploadId,
    string StorageKey,
    IReadOnlyList<string> PresignedUrls,
    long PartSizeBytes,
    int TotalParts);
