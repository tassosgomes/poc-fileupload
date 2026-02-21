using System.Net;
using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Formatting.Compact;
using tusdotnet;
using tusdotnet.Models.Configuration;
using tusdotnet.Models;
using tusdotnet.Stores;
using UploadPoc.Domain.Events;
using UploadPoc.API.Middleware;
using UploadPoc.API.Services;
using UploadPoc.Application.Commands;
using UploadPoc.Application.Consumers;
using UploadPoc.Application.Dtos;
using UploadPoc.Application.Handlers;
using UploadPoc.Application.Jobs;
using UploadPoc.Application.Validators;
using UploadPoc.Domain.Interfaces;
using UploadPoc.Infra.Messaging;
using UploadPoc.Infra.Persistence;
using UploadPoc.Infra.Persistence.Repositories;
using UploadPoc.Infra.Services;
using UploadPoc.Infra.Storage;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .Enrich.WithProperty("service.name", "upload-poc")
    .CreateLogger();

builder.Host.UseSerilog((context, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console(new CompactJsonFormatter())
        .Enrich.WithProperty("service.name", "upload-poc");
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Use: Bearer {token}"
    });

    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Missing Jwt:Secret configuration.");

if (jwtSecret.Length < 32)
{
    throw new InvalidOperationException("Jwt:Secret must have at least 32 characters.");
}

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "upload-poc";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "upload-poc";
var expirationHours = builder.Configuration.GetValue("Jwt:ExpirationHours", 8);
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
var tusStoragePath = builder.Configuration["TusStorage:Path"] ?? "/app/uploads";

builder.Services.AddSingleton(new JwtService(jwtSecret, jwtIssuer, jwtAudience, expirationHours));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireExpirationTime = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = signingKey,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.Zero
        };

        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();

                var problemDetails = new ProblemDetails
                {
                    Type = "https://datatracker.ietf.org/doc/html/rfc9457",
                    Title = "Unauthorized",
                    Status = StatusCodes.Status401Unauthorized,
                    Detail = "A valid Bearer token is required.",
                    Instance = context.Request.Path
                };

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(problemDetails);
            }
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("DefaultConnection")!, name: "postgresql")
    .AddRabbitMQ(
        rabbitConnectionString: BuildRabbitMqConnectionString(builder.Configuration),
        name: "rabbitmq")
    .AddAsyncCheck("minio", async cancellationToken =>
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await httpClient.GetAsync(BuildMinioHealthUri(builder.Configuration), cancellationToken);

        return response.IsSuccessStatusCode
            ? HealthCheckResult.Healthy("MinIO is reachable.")
            : HealthCheckResult.Unhealthy($"MinIO health endpoint returned HTTP {(int)response.StatusCode}.");
    });

builder.Services.AddScoped<IFileUploadRepository, FileUploadRepository>();
builder.Services.AddScoped<RegisterUploadHandler>();
builder.Services.AddScoped<InitiateMinioUploadHandler>();
builder.Services.AddScoped<CompleteUploadHandler>();
builder.Services.AddScoped<CancelUploadHandler>();
builder.Services.AddScoped<ListUploadsHandler>();
builder.Services.AddScoped<GetDownloadUrlHandler>();
builder.Services.AddScoped<UploadCompletedConsumer>();
builder.Services.AddScoped<UploadCompletedDlqConsumer>();
builder.Services.AddScoped<IValidator<RegisterUploadCommand>, RegisterUploadValidator>();
builder.Services.AddScoped<IValidator<CompleteMinioRequest>, CompleteMinioValidator>();
builder.Services.AddSingleton<IChecksumService, Sha256ChecksumService>();
builder.Services.AddSingleton<MinioStorageService>();
builder.Services.AddSingleton<IStorageService>(serviceProvider => serviceProvider.GetRequiredService<MinioStorageService>());
builder.Services.AddKeyedSingleton<IStorageService, TusDiskStorageService>("tus-disk");
builder.Services.AddKeyedSingleton<IStorageService>("minio", static (serviceProvider, _) =>
    serviceProvider.GetRequiredService<MinioStorageService>());
builder.Services.AddSingleton<IEventPublisher, RabbitMqPublisher>();
builder.Services.AddHostedService<RabbitMqConsumerHostedService>();
builder.Services.AddHostedService<OrphanCleanupJob>();

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    var minioStorageService = scope.ServiceProvider.GetRequiredService<MinioStorageService>();
    await minioStorageService.ConfigureBucketAsync(CancellationToken.None);
}

app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { message = "UploadPoc API is running." }));
app.MapTus("/upload/tus", _ =>
{
    var configuration = new DefaultTusConfiguration
    {
        Store = new TusDiskStore(tusStoragePath),
        MetadataParsingStrategy = MetadataParsingStrategy.AllowEmptyValues,
        MaxAllowedUploadSizeInBytesLong = 300L * 1024 * 1024 * 1024,
        Events = new Events
        {
            OnAuthorizeAsync = eventContext =>
            {
                var authorization = eventContext.HttpContext.Request.Headers.Authorization.ToString();
                if (string.IsNullOrWhiteSpace(authorization)
                    || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    eventContext.FailRequest(HttpStatusCode.Unauthorized);
                    return Task.CompletedTask;
                }

                var token = authorization["Bearer ".Length..].Trim();
                if (string.IsNullOrWhiteSpace(token))
                {
                    eventContext.FailRequest(HttpStatusCode.Unauthorized);
                    return Task.CompletedTask;
                }

                var jwtService = eventContext.HttpContext.RequestServices.GetRequiredService<JwtService>();
                var principal = jwtService.ValidateToken(token);
                if (principal is null)
                {
                    eventContext.FailRequest(HttpStatusCode.Unauthorized);
                    return Task.CompletedTask;
                }

                eventContext.HttpContext.User = principal;
                return Task.CompletedTask;
            },
            OnCreateCompleteAsync = async eventContext =>
            {
                if (!TryGetUploadId(eventContext.Metadata, out var uploadId))
                {
                    return;
                }

                var repository = eventContext.HttpContext.RequestServices.GetRequiredService<IFileUploadRepository>();
                var upload = await repository.GetByIdAsync(uploadId, eventContext.CancellationToken);
                if (upload is null)
                {
                    return;
                }

                upload.SetStorageKey(eventContext.FileId);
                await repository.UpdateAsync(upload, eventContext.CancellationToken);
            },
            OnFileCompleteAsync = async eventContext =>
            {
                var logger = eventContext.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                var repository = eventContext.HttpContext.RequestServices.GetRequiredService<IFileUploadRepository>();
                var publisher = eventContext.HttpContext.RequestServices.GetRequiredService<IEventPublisher>();

                var file = await eventContext.GetFileAsync();
                if (file is null)
                {
                    logger.LogWarning("TUS completion callback could not resolve file for FileId {FileId}.", eventContext.FileId);
                    return;
                }

                var metadata = await file.GetMetadataAsync(eventContext.CancellationToken);
                if (!TryGetUploadId(metadata, out var uploadId))
                {
                    logger.LogWarning("TUS completion callback missing uploadId metadata for FileId {FileId}.", eventContext.FileId);
                    return;
                }

                var upload = await repository.GetByIdAsync(uploadId, eventContext.CancellationToken);
                if (upload is null)
                {
                    logger.LogWarning("Upload {UploadId} was not found when processing TUS file completion.", uploadId);
                    return;
                }

                if (!string.Equals(upload.StorageKey, eventContext.FileId, StringComparison.Ordinal))
                {
                    upload.SetStorageKey(eventContext.FileId);
                }

                await repository.UpdateAsync(upload, eventContext.CancellationToken);

                await publisher.PublishUploadCompletedAsync(
                    new UploadCompletedEvent(
                        upload.Id,
                        eventContext.FileId,
                        upload.ExpectedSha256,
                        "TUS",
                        DateTime.UtcNow),
                    eventContext.CancellationToken);

                var durationMs = Math.Max(0, (DateTime.UtcNow - upload.CreatedAt).TotalMilliseconds);
                logger.LogInformation(
                    "TUS upload completed. UploadId={UploadId} FileName={FileName} DurationMs={DurationMs}",
                    upload.Id,
                    upload.FileName,
                    durationMs);
            }
        }
    };

    return Task.FromResult(configuration);
});
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

static string BuildRabbitMqConnectionString(IConfiguration configuration)
{
    var host = configuration["RabbitMQ:Host"] ?? "localhost";
    var port = configuration["RabbitMQ:Port"] ?? "5672";
    var username = configuration["RabbitMQ:Username"] ?? "guest";
    var password = configuration["RabbitMQ:Password"] ?? "guest";

    return $"amqp://{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}@{host}:{port}";
}

static Uri BuildMinioHealthUri(IConfiguration configuration)
{
    var endpoint = configuration["MinIO:Endpoint"] ?? "localhost:9000";
    var useSsl = configuration.GetValue("MinIO:UseSSL", false);
    var protocol = useSsl ? "https" : "http";
    var normalizedEndpoint = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        ? endpoint
        : $"{protocol}://{endpoint}";

    return new Uri($"{normalizedEndpoint.TrimEnd('/')}/minio/health/live");
}

static bool TryGetUploadId(IReadOnlyDictionary<string, Metadata> metadata, out Guid uploadId)
{
    uploadId = Guid.Empty;

    if (!metadata.TryGetValue("uploadId", out var uploadIdMetadata) || uploadIdMetadata.HasEmptyValue)
    {
        return false;
    }

    var uploadIdText = uploadIdMetadata.GetString(Encoding.UTF8);
    return Guid.TryParse(uploadIdText, out uploadId);
}
