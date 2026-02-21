namespace UploadPoc.Application.Commands;

public sealed record InitiateMinioUploadCommand(
    string FileName,
    long FileSizeBytes,
    string ContentType,
    string ExpectedSha256,
    string CreatedBy);
