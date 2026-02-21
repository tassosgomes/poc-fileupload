namespace UploadPoc.Application.Commands;

public sealed record CompleteUploadCommand(Guid UploadId, IReadOnlyList<CompleteUploadPart> Parts);

public sealed record CompleteUploadPart(int PartNumber, string ETag);
