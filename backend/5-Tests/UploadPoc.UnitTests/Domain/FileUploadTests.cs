using FluentAssertions;
using UploadPoc.Domain.Entities;
using UploadPoc.Domain.Enums;

namespace UploadPoc.UnitTests.Domain;

public class FileUploadTests
{
    [Fact]
    public void Create_ShouldSetStatusPending()
    {
        var upload = CreatePendingUpload();

        upload.Status.Should().Be(UploadStatus.Pending);
    }

    [Fact]
    public void MarkCompleted_WhenPending_ShouldSetStatusCompleted()
    {
        var upload = CreatePendingUpload();

        upload.MarkCompleted("a".PadLeft(64, 'a'), "uploads/key");

        upload.Status.Should().Be(UploadStatus.Completed);
        upload.CompletedAt.Should().NotBeNull();
        upload.StorageKey.Should().Be("uploads/key");
        upload.ActualSha256.Should().Be("a".PadLeft(64, 'a'));
    }

    [Fact]
    public void MarkCorrupted_WhenPending_ShouldSetStatusCorrupted()
    {
        var upload = CreatePendingUpload();

        upload.MarkCorrupted("b".PadLeft(64, 'b'));

        upload.Status.Should().Be(UploadStatus.Corrupted);
        upload.CompletedAt.Should().NotBeNull();
        upload.ActualSha256.Should().Be("b".PadLeft(64, 'b'));
    }

    [Fact]
    public void MarkCancelled_WhenPending_ShouldSetStatusCancelled()
    {
        var upload = CreatePendingUpload();

        upload.MarkCancelled();

        upload.Status.Should().Be(UploadStatus.Cancelled);
        upload.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkFailed_WhenPending_ShouldSetStatusFailed()
    {
        var upload = CreatePendingUpload();

        upload.MarkFailed("processing timeout");

        upload.Status.Should().Be(UploadStatus.Failed);
        upload.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkCompleted_WhenAlreadyCompleted_ShouldThrow()
    {
        var upload = CreatePendingUpload();
        upload.MarkCompleted("a".PadLeft(64, 'a'), "uploads/key");

        var action = () => upload.MarkCompleted("a".PadLeft(64, 'a'), "uploads/key");

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkCompleted_WhenCancelled_ShouldThrow()
    {
        var upload = CreatePendingUpload();
        upload.MarkCancelled();

        var action = () => upload.MarkCompleted("a".PadLeft(64, 'a'), "uploads/key");

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SetChecksum_ShouldStoreValue()
    {
        var upload = CreatePendingUpload();

        upload.MarkCompleted("c".PadLeft(64, 'c'), "uploads/key");

        upload.ActualSha256.Should().Be("c".PadLeft(64, 'c'));
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
}
