namespace UploadPoc.Application.Dtos;

public sealed record DownloadResult(
    string Scenario,
    string FileName,
    string ContentType,
    string? FilePath,
    string? PresignedUrl);
