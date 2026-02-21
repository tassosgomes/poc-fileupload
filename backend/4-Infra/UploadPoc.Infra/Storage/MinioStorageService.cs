using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using UploadPoc.Domain.Interfaces;

namespace UploadPoc.Infra.Storage;

public sealed class MinioStorageService : IStorageService
{
    private const int MaxRetryAttempts = 3;
    private const int PresignedUrlExpiryHours = 24;
    private const int PresignedDownloadUrlExpiryHours = 1;

    private readonly IAmazonS3 _s3Client;
    private readonly IChecksumService _checksumService;
    private readonly ILogger<MinioStorageService> _logger;
    private readonly AsyncRetryPolicy _retryPolicy;

    public MinioStorageService(
        IConfiguration configuration,
        IChecksumService checksumService,
        ILogger<MinioStorageService> logger)
    {
        _checksumService = checksumService;
        _logger = logger;

        var endpoint = configuration["MinIO:Endpoint"]
            ?? throw new InvalidOperationException("Missing MinIO:Endpoint configuration.");

        var accessKey = configuration["MinIO:AccessKey"]
            ?? throw new InvalidOperationException("Missing MinIO:AccessKey configuration.");

        var secretKey = configuration["MinIO:SecretKey"]
            ?? throw new InvalidOperationException("Missing MinIO:SecretKey configuration.");

        var useSsl = bool.TryParse(configuration["MinIO:UseSSL"], out var parsedUseSsl) && parsedUseSsl;

        BucketName = configuration["MinIO:BucketName"]
            ?? throw new InvalidOperationException("Missing MinIO:BucketName configuration.");

        var serviceUrl = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : $"{(useSsl ? "https" : "http")}://{endpoint}";

        var config = new AmazonS3Config
        {
            ServiceURL = serviceUrl,
            ForcePathStyle = true,
            UseHttp = !useSsl
        };

        _s3Client = new AmazonS3Client(accessKey, secretKey, config);

        _retryPolicy = Policy
            .Handle<AmazonS3Exception>()
            .Or<HttpRequestException>()
            .WaitAndRetryAsync(
                retryCount: MaxRetryAttempts,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetry: (exception, delay, retryCount, _) =>
                {
                    _logger.LogWarning(
                        exception,
                        "Retry {RetryCount}/{MaxRetryAttempts} calling MinIO in {DelaySeconds}s.",
                        retryCount,
                        MaxRetryAttempts,
                        delay.TotalSeconds);
                });
    }

    public string BucketName { get; }

    public async Task ConfigureBucketAsync(CancellationToken cancellationToken)
    {
        await EnsureBucketExistsAsync(cancellationToken);

        var corsConfiguration = new CORSConfiguration
        {
            Rules =
            [
                new CORSRule
                {
                    AllowedHeaders = ["*"],
                    AllowedMethods = ["PUT"],
                    // TODO: Move CORS origins to environment configuration for production deployments.
                    AllowedOrigins = ["http://localhost:5173"],
                    ExposeHeaders = ["ETag"]
                }
            ]
        };

        await _retryPolicy.ExecuteAsync(ct => _s3Client.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = BucketName,
            Configuration = corsConfiguration
        }, ct), cancellationToken);

        var lifecycleConfiguration = new LifecycleConfiguration
        {
            Rules =
            [
                new LifecycleRule
                {
                    Id = "abort-incomplete-multipart-after-3-days",
                    Status = LifecycleRuleStatus.Enabled,
                    AbortIncompleteMultipartUpload = new LifecycleRuleAbortIncompleteMultipartUpload
                    {
                        DaysAfterInitiation = 3
                    }
                }
            ]
        };

        await _retryPolicy.ExecuteAsync(ct => _s3Client.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = BucketName,
            Configuration = lifecycleConfiguration
        }, ct), cancellationToken);

        _logger.LogInformation("MinIO bucket setup completed for {BucketName}.", BucketName);
    }

    public async Task<string> InitiateMultipartUploadAsync(string bucketName, string key, CancellationToken cancellationToken)
    {
        var response = await _retryPolicy.ExecuteAsync(ct => _s3Client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucketName,
            Key = key
        }, ct), cancellationToken);

        return response.UploadId;
    }

    public Task<IReadOnlyList<string>> GeneratePresignedUrlsAsync(
        string bucketName,
        string key,
        string uploadId,
        int totalParts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (totalParts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalParts), "Total parts must be greater than zero.");
        }

        var urls = new List<string>(totalParts);

        for (var partNumber = 1; partNumber <= totalParts; partNumber++)
        {
            var presignedUrl = _s3Client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = bucketName,
                Key = key,
                Verb = HttpVerb.PUT,
                Expires = DateTime.UtcNow.AddHours(PresignedUrlExpiryHours),
                PartNumber = partNumber,
                UploadId = uploadId
            });

            urls.Add(presignedUrl);
        }

        return Task.FromResult<IReadOnlyList<string>>(urls);
    }

    public string GeneratePresignedDownloadUrl(string key, string? fileName = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Storage key cannot be null or whitespace.", nameof(key));
        }

        var resolvedFileName = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(key) : fileName.Trim();

        return _s3Client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = BucketName,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddHours(PresignedDownloadUrlExpiryHours),
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentDisposition = $"attachment; filename=\"{resolvedFileName}\""
            }
        });
    }

    public async Task CompleteMultipartUploadAsync(
        string bucketName,
        string key,
        string uploadId,
        IReadOnlyList<StoragePartInfo> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count == 0)
        {
            throw new ArgumentException("At least one multipart ETag is required.", nameof(parts));
        }

        var orderedPartEtags = parts
            .OrderBy(part => part.PartNumber)
            .Select(part => new PartETag(part.PartNumber, part.ETag.Trim('"')))
            .ToList();

        await _retryPolicy.ExecuteAsync(ct => _s3Client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucketName,
            Key = key,
            UploadId = uploadId,
            PartETags = orderedPartEtags
        }, ct), cancellationToken);
    }

    public async Task AbortMultipartUploadAsync(string bucketName, string key, string uploadId, CancellationToken cancellationToken)
    {
        await _retryPolicy.ExecuteAsync(ct => _s3Client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
        {
            BucketName = bucketName,
            Key = key,
            UploadId = uploadId
        }, ct), cancellationToken);
    }

    public async Task<string> ComputeSha256Async(string storageKey, CancellationToken cancellationToken)
    {
        await using var stream = await GetObjectStreamAsync(storageKey, cancellationToken);
        return await _checksumService.ComputeSha256Async(stream, cancellationToken);
    }

    public async Task<Stream> GetObjectStreamAsync(string storageKey, CancellationToken cancellationToken)
    {
        var response = await _retryPolicy.ExecuteAsync(ct => _s3Client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = BucketName,
            Key = storageKey
        }, ct), cancellationToken);

        return new S3ObjectResponseStream(response);
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        await _retryPolicy.ExecuteAsync(ct => _s3Client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = BucketName,
            Key = storageKey
        }, ct), cancellationToken);
    }

    public async Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken)
    {
        try
        {
            await _retryPolicy.ExecuteAsync(ct => _s3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = BucketName,
                Key = storageKey
            }, ct), cancellationToken);

            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task EnsureBucketExistsAsync(CancellationToken cancellationToken)
    {
        var exists = await _retryPolicy.ExecuteAsync(ct => AmazonS3Util.DoesS3BucketExistV2Async(_s3Client, BucketName), cancellationToken);
        if (exists)
        {
            return;
        }

        await _retryPolicy.ExecuteAsync(ct => _s3Client.PutBucketAsync(new PutBucketRequest
        {
            BucketName = BucketName
        }, ct), cancellationToken);
    }

    private sealed class S3ObjectResponseStream : Stream
    {
        private readonly GetObjectResponse _response;
        private readonly Stream _inner;

        public S3ObjectResponseStream(GetObjectResponse response)
        {
            _response = response;
            _inner = response.ResponseStream;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush()
        {
            _inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _inner.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            _inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return _inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return _inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            _response.Dispose();
            await base.DisposeAsync();
        }
    }
}
