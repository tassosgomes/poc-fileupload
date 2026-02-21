using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UploadPoc.Domain.Entities;

namespace UploadPoc.Infra.Persistence.Configurations;

public class FileUploadConfiguration : IEntityTypeConfiguration<FileUpload>
{
    public void Configure(EntityTypeBuilder<FileUpload> builder)
    {
        builder.ToTable("file_uploads");

        builder.HasKey(fileUpload => fileUpload.Id);

        builder.Property(fileUpload => fileUpload.Id)
            .HasColumnName("id");

        builder.Property(fileUpload => fileUpload.FileName)
            .HasColumnName("file_name")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(fileUpload => fileUpload.FileSizeBytes)
            .HasColumnName("file_size_bytes")
            .IsRequired();

        builder.Property(fileUpload => fileUpload.ContentType)
            .HasColumnName("content_type")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(fileUpload => fileUpload.ExpectedSha256)
            .HasColumnName("expected_sha256")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(fileUpload => fileUpload.ActualSha256)
            .HasColumnName("actual_sha256")
            .HasMaxLength(64);

        builder.Property(fileUpload => fileUpload.UploadScenario)
            .HasColumnName("upload_scenario")
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(fileUpload => fileUpload.StorageKey)
            .HasColumnName("storage_key")
            .HasMaxLength(500);

        builder.Property(fileUpload => fileUpload.MinioUploadId)
            .HasColumnName("minio_upload_id")
            .HasMaxLength(200);

        builder.Property(fileUpload => fileUpload.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(fileUpload => fileUpload.CreatedBy)
            .HasColumnName("created_by")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(fileUpload => fileUpload.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(fileUpload => fileUpload.CompletedAt)
            .HasColumnName("completed_at");

        builder.HasIndex(fileUpload => fileUpload.Status)
            .HasDatabaseName("IX_file_uploads_status");

        builder.HasIndex(fileUpload => fileUpload.CreatedBy)
            .HasDatabaseName("IX_file_uploads_created_by");
    }
}
