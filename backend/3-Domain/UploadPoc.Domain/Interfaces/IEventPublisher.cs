using UploadPoc.Domain.Events;

namespace UploadPoc.Domain.Interfaces;

public interface IEventPublisher
{
    Task PublishUploadCompletedAsync(UploadCompletedEvent @event, CancellationToken cancellationToken);

    Task PublishUploadTimeoutAsync(Guid uploadId, CancellationToken cancellationToken);
}
