using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using UploadPoc.Application.Consumers;
using UploadPoc.Domain.Entities;
using UploadPoc.Domain.Enums;
using UploadPoc.Domain.Events;
using UploadPoc.Domain.Interfaces;

namespace UploadPoc.UnitTests.Application;

public class UploadCompletedConsumerTests
{
    private readonly Mock<IFileUploadRepository> _repositoryMock = new();
    private readonly Mock<IStorageService> _tusStorageMock = new();
    private readonly Mock<IStorageService> _minioStorageMock = new();
    private readonly Mock<ILogger<UploadCompletedConsumer>> _loggerMock = new();

    [Fact]
    public async Task HandleMessage_WhenChecksumMatches_ShouldMarkCompleted()
    {
        var upload = CreatePendingUpload();
        var expectedSha256 = "a".PadLeft(64, 'a');
        var message = CreateEvent(upload.Id, expectedSha256, "TUS", "uploads/file-1");

        _repositoryMock
            .Setup(repository => repository.GetByIdAsync(upload.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(upload);
        _tusStorageMock
            .Setup(storage => storage.ComputeSha256Async(message.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedSha256);

        var consumer = CreateConsumer();

        await consumer.ProcessAsync(message, CancellationToken.None);

        upload.Status.Should().Be(UploadStatus.Completed);
        upload.ActualSha256.Should().Be(expectedSha256);
        upload.StorageKey.Should().Be(message.StorageKey);
        _repositoryMock.Verify(repository => repository.UpdateAsync(upload, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleMessage_WhenChecksumMismatch_ShouldMarkCorrupted()
    {
        var upload = CreatePendingUpload();
        var message = CreateEvent(upload.Id, "a".PadLeft(64, 'a'), "TUS", "uploads/file-1");
        var actualSha256 = "b".PadLeft(64, 'b');

        _repositoryMock
            .Setup(repository => repository.GetByIdAsync(upload.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(upload);
        _tusStorageMock
            .Setup(storage => storage.ComputeSha256Async(message.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(actualSha256);

        var consumer = CreateConsumer();

        await consumer.ProcessAsync(message, CancellationToken.None);

        upload.Status.Should().Be(UploadStatus.Corrupted);
        upload.ActualSha256.Should().Be(actualSha256);
        _repositoryMock.Verify(repository => repository.UpdateAsync(upload, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleMessage_WhenUploadNotFound_ShouldThrow()
    {
        var uploadId = Guid.NewGuid();
        var message = CreateEvent(uploadId, "a".PadLeft(64, 'a'), "TUS", "uploads/file-1");

        _repositoryMock
            .Setup(repository => repository.GetByIdAsync(uploadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileUpload?)null);

        var consumer = CreateConsumer();

        var action = async () => await consumer.ProcessAsync(message, CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>();
        _repositoryMock.Verify(repository => repository.UpdateAsync(It.IsAny<FileUpload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleMessage_WhenStorageThrows_ShouldNotUpdateStatus()
    {
        var upload = CreatePendingUpload();
        var message = CreateEvent(upload.Id, "a".PadLeft(64, 'a'), "MINIO", "uploads/file-1");

        _repositoryMock
            .Setup(repository => repository.GetByIdAsync(upload.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(upload);
        _minioStorageMock
            .Setup(storage => storage.ComputeSha256Async(message.StorageKey, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("storage unavailable"));

        var consumer = CreateConsumer();

        var action = async () => await consumer.ProcessAsync(message, CancellationToken.None);

        await action.Should().ThrowAsync<IOException>();
        upload.Status.Should().Be(UploadStatus.Pending);
        _repositoryMock.Verify(repository => repository.UpdateAsync(It.IsAny<FileUpload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private UploadCompletedConsumer CreateConsumer()
    {
        return new UploadCompletedConsumer(
            _repositoryMock.Object,
            _tusStorageMock.Object,
            _minioStorageMock.Object,
            _loggerMock.Object);
    }

    private static FileUpload CreatePendingUpload()
    {
        return new FileUpload(
            "sample.txt",
            1024,
            "text/plain",
            "d".PadLeft(64, 'd'),
            "TUS",
            "tester");
    }

    private static UploadCompletedEvent CreateEvent(Guid uploadId, string expectedSha256, string scenario, string storageKey)
    {
        return new UploadCompletedEvent(uploadId, storageKey, expectedSha256, scenario, DateTime.UtcNow);
    }
}
