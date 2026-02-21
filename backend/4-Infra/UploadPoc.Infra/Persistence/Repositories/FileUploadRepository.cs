using Microsoft.EntityFrameworkCore;
using UploadPoc.Domain.Entities;
using UploadPoc.Domain.Enums;
using UploadPoc.Domain.Interfaces;

namespace UploadPoc.Infra.Persistence.Repositories;

public class FileUploadRepository : IFileUploadRepository
{
    private readonly AppDbContext _context;

    public FileUploadRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<FileUpload> AddAsync(FileUpload upload, CancellationToken cancellationToken)
    {
        _context.FileUploads.Add(upload);
        await _context.SaveChangesAsync(cancellationToken);

        return upload;
    }

    public async Task<FileUpload?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        return await _context.FileUploads.FindAsync([id], cancellationToken);
    }

    public async Task<IReadOnlyList<FileUpload>> GetAllAsync(CancellationToken cancellationToken)
    {
        return await _context.FileUploads
            .AsNoTracking()
            .OrderByDescending(fileUpload => fileUpload.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FileUpload>> GetPendingOlderThanAsync(TimeSpan age, CancellationToken cancellationToken)
    {
        var threshold = DateTime.UtcNow - age;

        return await _context.FileUploads
            .AsNoTracking()
            .Where(fileUpload => fileUpload.Status == UploadStatus.Pending && fileUpload.CreatedAt < threshold)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ExistsByStorageKeyAsync(string storageKey, CancellationToken cancellationToken)
    {
        return await _context.FileUploads
            .AsNoTracking()
            .AnyAsync(fileUpload => fileUpload.StorageKey == storageKey, cancellationToken);
    }

    public async Task UpdateAsync(FileUpload upload, CancellationToken cancellationToken)
    {
        _context.FileUploads.Update(upload);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
