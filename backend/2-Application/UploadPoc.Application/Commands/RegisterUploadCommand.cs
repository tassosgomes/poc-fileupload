namespace UploadPoc.Application.Commands;

public sealed record RegisterUploadCommand(
    string FileName,
    long FileSizeBytes,
    string ContentType,
    string ExpectedSha256,
    string UploadScenario,
    string CreatedBy);
