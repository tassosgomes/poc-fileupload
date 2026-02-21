namespace UploadPoc.Application.Dtos;

public sealed record UploadDto(
    Guid Id,
    string FileName,
    long FileSizeBytes,
    string ContentType,
    string ExpectedSha256,
    string? ActualSha256,
    string UploadScenario,
    string? StorageKey,
    string Status,
    string CreatedBy,
    DateTime CreatedAt,
    DateTime? CompletedAt);
