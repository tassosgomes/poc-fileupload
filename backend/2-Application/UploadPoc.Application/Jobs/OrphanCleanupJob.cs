using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UploadPoc.Domain.Interfaces;

namespace UploadPoc.Application.Jobs;

public sealed class OrphanCleanupJob : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(24);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OrphanCleanupJob> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _timeout;
    private readonly string _tusStoragePath;

    public OrphanCleanupJob(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<OrphanCleanupJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _interval = ResolveInterval(configuration);
        _timeout = ResolveTimeout(configuration);
        _tusStoragePath = configuration["TusStorage:Path"] ?? "/app/uploads";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunCleanupCycleAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCleanupCycleAsync(stoppingToken);
        }
    }

    private async Task RunCleanupCycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IFileUploadRepository>();
            var eventPublisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
            var tusStorage = scope.ServiceProvider.GetRequiredKeyedService<IStorageService>("tus-disk");

            await DetectTimeoutUploadsAsync(repository, eventPublisher, cancellationToken);
            await DetectTusOrphansAsync(repository, tusStorage, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Erro no job de limpeza de órfãos.");
        }
    }

    private async Task DetectTimeoutUploadsAsync(
        IFileUploadRepository repository,
        IEventPublisher eventPublisher,
        CancellationToken cancellationToken)
    {
        var pendingUploads = await repository.GetPendingOlderThanAsync(_timeout, cancellationToken);
        foreach (var upload in pendingUploads)
        {
            await eventPublisher.PublishUploadTimeoutAsync(upload.Id, cancellationToken);

            _logger.LogWarning(
                "Cleanup action: {UploadId} {Action} {StorageKey} {Timestamp}",
                upload.Id,
                "upload_timeout_published",
                upload.StorageKey,
                DateTime.UtcNow);
        }
    }

    private async Task DetectTusOrphansAsync(
        IFileUploadRepository repository,
        IStorageService tusStorage,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_tusStoragePath))
        {
            return;
        }

        var filePaths = Directory.GetFiles(_tusStoragePath, "*", SearchOption.TopDirectoryOnly);
        foreach (var filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var storageKey = Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(storageKey) || IsTusMetadataFile(storageKey))
            {
                continue;
            }

            var existsInDatabase = await repository.ExistsByStorageKeyAsync(storageKey, cancellationToken);
            if (existsInDatabase)
            {
                continue;
            }

            await tusStorage.DeleteAsync(storageKey, cancellationToken);

            _logger.LogInformation(
                "Órfão removido: { storageKey: {StorageKey}, action: 'deleted_from_disk' }",
                storageKey);
            _logger.LogInformation(
                "Cleanup action: {UploadId} {Action} {StorageKey} {Timestamp}",
                null,
                "deleted_from_disk",
                storageKey,
                DateTime.UtcNow);
        }
    }

    private static bool IsTusMetadataFile(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(extension);
    }

    private static TimeSpan ResolveInterval(IConfiguration configuration)
    {
        var minutes = configuration.GetValue<int?>("OrphanCleanup:IntervalMinutes") ?? 60;
        return minutes > 0 ? TimeSpan.FromMinutes(minutes) : DefaultInterval;
    }

    private static TimeSpan ResolveTimeout(IConfiguration configuration)
    {
        var hours = configuration.GetValue<int?>("OrphanCleanup:TimeoutHours") ?? 24;
        return hours > 0 ? TimeSpan.FromHours(hours) : DefaultTimeout;
    }
}
