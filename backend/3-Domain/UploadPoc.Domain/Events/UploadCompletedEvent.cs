namespace UploadPoc.Domain.Events;

public sealed record UploadCompletedEvent(
    Guid UploadId,
    string StorageKey,
    string ExpectedSha256,
    string UploadScenario,
    DateTime Timestamp);
