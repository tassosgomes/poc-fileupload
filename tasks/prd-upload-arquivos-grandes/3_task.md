---
status: pending
parallelizable: false
blocked_by: ["2.0"]
---

<task_context>
<domain>infra/database</domain>
<type>implementation</type>
<scope>core_feature</scope>
<complexity>medium</complexity>
<dependencies>database</dependencies>
<unblocks>"4.0", "5.0", "6.0"</unblocks>
</task_context>

# Tarefa 3.0: Camada Infra — PostgreSQL e EF Core

## Visão Geral

Implementar a camada de persistência usando Entity Framework Core com PostgreSQL (Npgsql). Inclui o `AppDbContext`, a configuração de mapeamento da entidade `FileUpload` para a tabela `file_uploads`, a implementação do repositório `FileUploadRepository` e a migration inicial.

As migrations serão executadas automaticamente no startup da aplicação via `Database.MigrateAsync()`.

## Requisitos

- A tabela `file_uploads` deve seguir o schema definido na techspec (nomes snake_case, tipos corretos, índices).
- O repositório deve implementar `IFileUploadRepository` definido no Domain.
- A connection string deve ser configurável via environment variable (`ConnectionStrings__DefaultConnection`).
- Migrations devem rodar automaticamente no startup.

## Subtarefas

- [ ] 3.1 Criar `AppDbContext` em `4-Infra/UploadPoc.Infra/Persistence/AppDbContext.cs`:
  - DbSet<FileUpload> FileUploads
  - Override `OnModelCreating` para aplicar configurações
- [ ] 3.2 Criar `FileUploadConfiguration` em `4-Infra/UploadPoc.Infra/Persistence/Configurations/FileUploadConfiguration.cs`:
  - Mapeamento para tabela `file_uploads`
  - Todas as colunas com tipos/constraints conforme techspec
  - Conversão de `UploadStatus` para string
  - Índices: `IX_file_uploads_status`, `IX_file_uploads_created_by`
- [ ] 3.3 Criar `FileUploadRepository` em `4-Infra/UploadPoc.Infra/Persistence/Repositories/FileUploadRepository.cs`:
  - Implementar `IFileUploadRepository`
  - `AddAsync`: Salvar nova entidade
  - `GetByIdAsync`: Buscar por ID (retorna null se não existe)
  - `GetAllAsync`: Listar todos (ordenado por `CreatedAt` desc)
  - `GetPendingOlderThanAsync`: Buscar uploads `Pending` com `CreatedAt` mais antigo que o threshold
  - `UpdateAsync`: Atualizar entidade existente
- [ ] 3.4 Registrar `AppDbContext` e `IFileUploadRepository` no DI container (`Program.cs`)
- [ ] 3.5 Gerar migration inicial via `dotnet ef migrations add InitialCreate`
- [ ] 3.6 Adicionar `Database.MigrateAsync()` no startup do `Program.cs`
- [ ] 3.7 Testar que a migration roda corretamente com o PostgreSQL do Docker Compose

## Sequenciamento

- Bloqueado por: 2.0 (Domain Layer — precisa das entidades e interfaces)
- Desbloqueia: 4.0 (RabbitMQ), 5.0 (JWT Auth), 6.0 (Registro de Metadados)
- Paralelizável: Não (4.0 e 5.0 dependem desta tarefa)

## Detalhes de Implementação

### AppDbContext

```csharp
public class AppDbContext : DbContext
{
    public DbSet<FileUpload> FileUploads => Set<FileUpload>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
```

### FileUploadConfiguration

Seguir exatamente o código da techspec:

```csharp
public class FileUploadConfiguration : IEntityTypeConfiguration<FileUpload>
{
    public void Configure(EntityTypeBuilder<FileUpload> builder)
    {
        builder.ToTable("file_uploads");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.FileName).HasMaxLength(500).IsRequired();
        builder.Property(f => f.FileSizeBytes).IsRequired();
        builder.Property(f => f.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(f => f.ExpectedSha256).HasMaxLength(64).IsRequired();
        builder.Property(f => f.ActualSha256).HasMaxLength(64);
        builder.Property(f => f.UploadScenario).HasMaxLength(10).IsRequired();
        builder.Property(f => f.StorageKey).HasMaxLength(500);
        builder.Property(f => f.MinioUploadId).HasMaxLength(200);
        builder.Property(f => f.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();
        builder.Property(f => f.CreatedBy).HasMaxLength(100).IsRequired();
        builder.Property(f => f.CreatedAt).IsRequired();

        builder.HasIndex(f => f.Status).HasDatabaseName("IX_file_uploads_status");
        builder.HasIndex(f => f.CreatedBy).HasDatabaseName("IX_file_uploads_created_by");
    }
}
```

### FileUploadRepository

```csharp
public class FileUploadRepository : IFileUploadRepository
{
    private readonly AppDbContext _context;

    public FileUploadRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<FileUpload> AddAsync(FileUpload upload, CancellationToken ct)
    {
        _context.FileUploads.Add(upload);
        await _context.SaveChangesAsync(ct);
        return upload;
    }

    public async Task<FileUpload?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        return await _context.FileUploads.FindAsync(new object[] { id }, ct);
    }

    public async Task<IReadOnlyList<FileUpload>> GetAllAsync(CancellationToken ct)
    {
        return await _context.FileUploads
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<FileUpload>> GetPendingOlderThanAsync(TimeSpan age, CancellationToken ct)
    {
        var threshold = DateTime.UtcNow - age;
        return await _context.FileUploads
            .Where(f => f.Status == UploadStatus.Pending && f.CreatedAt < threshold)
            .ToListAsync(ct);
    }

    public async Task UpdateAsync(FileUpload upload, CancellationToken ct)
    {
        _context.FileUploads.Update(upload);
        await _context.SaveChangesAsync(ct);
    }
}
```

### Registro no DI (Program.cs)

```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<IFileUploadRepository, FileUploadRepository>();
```

### Auto-Migration no Startup

```csharp
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}
```

## Critérios de Sucesso

- Tabela `file_uploads` é criada automaticamente no PostgreSQL ao iniciar a aplicação
- Todos os campos seguem os tipos e constraints da techspec (varchar lengths, NOT NULL, etc.)
- Índices `IX_file_uploads_status` e `IX_file_uploads_created_by` são criados
- `FileUploadRepository` implementa todos os 5 métodos de `IFileUploadRepository`
- `GetPendingOlderThanAsync` retorna apenas uploads com status `Pending` mais antigos que o threshold
- Migration roda sem erro com o PostgreSQL do Docker Compose (porta 5432)
