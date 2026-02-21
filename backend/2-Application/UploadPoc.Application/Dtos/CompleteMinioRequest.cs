namespace UploadPoc.Application.Dtos;

public sealed record CompleteMinioRequest(Guid UploadId, IReadOnlyList<PartETagDto> Parts);

public sealed record PartETagDto(int PartNumber, string ETag);
