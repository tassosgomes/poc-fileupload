using UploadPoc.Domain.Events;

namespace UploadPoc.Application.Consumers;

public interface IIntegrityCheckHandler
{
    Task HandleAsync(UploadCompletedEvent @event, CancellationToken cancellationToken);
}
