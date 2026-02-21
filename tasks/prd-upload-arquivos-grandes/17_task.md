---
status: pending
parallelizable: true
blocked_by: ["9.0"]
---

<task_context>
<domain>backend/testing</domain>
<type>testing</type>
<scope>unit-tests</scope>
<complexity>medium</complexity>
<dependencies>xUnit, Moq, AwesomeAssertions</dependencies>
<unblocks>none</unblocks>
</task_context>

# Tarefa 17.0: Testes Unitários

## Visão Geral

Implementar testes unitários cobrindo as regras de negócio críticas da POC: transições de estado da entidade `FileUpload`, lógica do consumer de integridade SHA-256 (match e mismatch), e validações do FluentValidation. Framework: **xUnit + Moq + AwesomeAssertions**. Escopo reduzido para POC — sem testes de integração ou E2E.

## Requisitos

- Cobertura das transições de estado válidas e inválidas da entidade `FileUpload`
- Cobertura do consumer `UploadCompletedConsumer` (cenários: SHA-256 match → `Completed`, mismatch → `Corrupted`)
- Cobertura dos validators do FluentValidation (`RegisterUploadValidator`)
- Todos os testes devem ser autocontidos (sem dependências externas)
- Framework: xUnit para testes, Moq para mocking, AwesomeAssertions para assertivas fluentes

## Subtarefas

- [ ] 17.1 Configurar projeto `UploadPoc.Tests`:
  - Criar projeto xUnit no diretório `tests/UploadPoc.Tests/`
  - Adicionar packages: `xunit`, `xunit.runner.visualstudio`, `Moq`, `AwesomeAssertions`
  - Adicionar referências aos projetos `Domain`, `Application`, `Infra`
  - Verificar que `dotnet test` executa com sucesso (0 testes)

- [ ] 17.2 Criar testes da entidade `FileUpload`:
  - **Arquivo**: `tests/UploadPoc.Tests/Domain/FileUploadTests.cs`
  - Classe: `FileUploadTests`
  - Testes:
    - `Create_ShouldSetStatusPending()` — nova instância tem status `Pending`
    - `MarkCompleted_WhenPending_ShouldSetStatusCompleted()` — transição válida
    - `MarkCorrupted_WhenPending_ShouldSetStatusCorrupted()` — transição válida
    - `MarkCancelled_WhenPending_ShouldSetStatusCancelled()` — transição válida
    - `MarkFailed_WhenPending_ShouldSetStatusFailed()` — transição válida
    - `MarkCompleted_WhenAlreadyCompleted_ShouldThrow()` — transição inválida
    - `MarkCompleted_WhenCancelled_ShouldThrow()` — transição inválida
    - `SetChecksum_ShouldStoreValue()` — verifica que checksum é salvo

- [ ] 17.3 Criar testes do `UploadCompletedConsumer`:
  - **Arquivo**: `tests/UploadPoc.Tests/Application/UploadCompletedConsumerTests.cs`
  - Classe: `UploadCompletedConsumerTests`
  - Mocks: `IFileUploadRepository`, `IStorageService`, `IChecksumService`
  - Testes:
    - `HandleMessage_WhenChecksumMatches_ShouldMarkCompleted()`:
      - Mock: storage retorna stream, checksum calcula SHA-256 igual ao front
      - Assert: entity.Status == Completed, repository.Update chamado
    - `HandleMessage_WhenChecksumMismatch_ShouldMarkCorrupted()`:
      - Mock: checksum retorna valor diferente
      - Assert: entity.Status == Corrupted, repository.Update chamado
    - `HandleMessage_WhenUploadNotFound_ShouldThrow()`:
      - Mock: repository.GetByIdAsync retorna null
      - Assert: lança exceção apropriada
    - `HandleMessage_WhenStorageThrows_ShouldNotUpdateStatus()`:
      - Mock: storage lança IOException
      - Assert: repository.Update NÃO chamado, exceção propaga

- [ ] 17.4 Criar testes do `RegisterUploadValidator`:
  - **Arquivo**: `tests/UploadPoc.Tests/Application/RegisterUploadValidatorTests.cs`
  - Classe: `RegisterUploadValidatorTests`
  - Testes:
    - `Validate_ValidCommand_ShouldPass()` — fileName, fileSize, provider válidos
    - `Validate_EmptyFileName_ShouldFail()` — fileName vazio
    - `Validate_NegativeFileSize_ShouldFail()` — fileSize < 0
    - `Validate_ZeroFileSize_ShouldFail()` — fileSize == 0
    - `Validate_InvalidProvider_ShouldFail()` — provider diferente de "tus" ou "minio"
    - `Validate_FileSizeExceedsLimit_ShouldFail()` — > 250 GB
    - `Validate_FileNameTooLong_ShouldFail()` — > 255 chars
    - `Validate_MissingSha256_ShouldFail()` — sha256 vazio/null

- [ ] 17.5 Verificar que todos os testes passam:
  - Executar `dotnet test` na raiz da solution
  - Verificar output: todos os testes green
  - Mínimo: 20 testes unitários

## Sequenciamento

- Bloqueado por: 9.0 (Consumer SHA-256 - precisa existir para ser testado)
- Desbloqueia: nenhum (tarefa final)
- Paralelizável: Sim (pode ser feito em paralelo com 16.0)

## Detalhes de Implementação

### Estrutura do Projeto de Testes

```
tests/
└── UploadPoc.Tests/
    ├── UploadPoc.Tests.csproj
    ├── Domain/
    │   └── FileUploadTests.cs
    └── Application/
        ├── UploadCompletedConsumerTests.cs
        └── RegisterUploadValidatorTests.cs
```

### Exemplo: FileUploadTests

```csharp
using UploadPoc.Domain.Entities;
using UploadPoc.Domain.Enums;

namespace UploadPoc.Tests.Domain;

public class FileUploadTests
{
    [Fact]
    public void Create_ShouldSetStatusPending()
    {
        // Arrange & Act
        var upload = new FileUpload("test.zip", 1024, "tus", "abc123sha256");

        // Assert
        upload.Status.Should().Be(UploadStatus.Pending);
        upload.FileName.Should().Be("test.zip");
        upload.FileSize.Should().Be(1024);
    }

    [Fact]
    public void MarkCompleted_WhenPending_ShouldSetStatusCompleted()
    {
        var upload = new FileUpload("test.zip", 1024, "tus", "abc123sha256");

        upload.MarkCompleted("sha256-confirmed");

        upload.Status.Should().Be(UploadStatus.Completed);
        upload.ServerChecksum.Should().Be("sha256-confirmed");
    }

    [Fact]
    public void MarkCompleted_WhenAlreadyCompleted_ShouldThrow()
    {
        var upload = new FileUpload("test.zip", 1024, "tus", "abc123sha256");
        upload.MarkCompleted("sha256");

        var act = () => upload.MarkCompleted("sha256");

        act.Should().Throw<InvalidOperationException>();
    }
}
```

### Exemplo: UploadCompletedConsumerTests

```csharp
using Moq;
using UploadPoc.Domain.Entities;
using UploadPoc.Domain.Interfaces;
using UploadPoc.Application.Consumers;

namespace UploadPoc.Tests.Application;

public class UploadCompletedConsumerTests
{
    private readonly Mock<IFileUploadRepository> _repoMock = new();
    private readonly Mock<IStorageService> _storageMock = new();
    private readonly Mock<IChecksumService> _checksumMock = new();

    [Fact]
    public async Task HandleMessage_WhenChecksumMatches_ShouldMarkCompleted()
    {
        // Arrange
        var upload = new FileUpload("test.zip", 1024, "tus", "abc123");
        _repoMock.Setup(r => r.GetByIdAsync(upload.Id)).ReturnsAsync(upload);
        _storageMock.Setup(s => s.GetFileStreamAsync(It.IsAny<string>()))
            .ReturnsAsync(new MemoryStream());
        _checksumMock.Setup(c => c.ComputeSha256Async(It.IsAny<Stream>()))
            .ReturnsAsync("abc123");

        var consumer = new UploadCompletedConsumer(
            _repoMock.Object, _storageMock.Object, _checksumMock.Object);

        // Act
        await consumer.HandleAsync(upload.Id);

        // Assert
        upload.Status.Should().Be(UploadStatus.Completed);
        _repoMock.Verify(r => r.UpdateAsync(upload), Times.Once);
    }

    [Fact]
    public async Task HandleMessage_WhenChecksumMismatch_ShouldMarkCorrupted()
    {
        var upload = new FileUpload("test.zip", 1024, "tus", "abc123");
        _repoMock.Setup(r => r.GetByIdAsync(upload.Id)).ReturnsAsync(upload);
        _storageMock.Setup(s => s.GetFileStreamAsync(It.IsAny<string>()))
            .ReturnsAsync(new MemoryStream());
        _checksumMock.Setup(c => c.ComputeSha256Async(It.IsAny<Stream>()))
            .ReturnsAsync("DIFFERENT_HASH");

        var consumer = new UploadCompletedConsumer(
            _repoMock.Object, _storageMock.Object, _checksumMock.Object);

        await consumer.HandleAsync(upload.Id);

        upload.Status.Should().Be(UploadStatus.Corrupted);
        _repoMock.Verify(r => r.UpdateAsync(upload), Times.Once);
    }
}
```

### Exemplo: RegisterUploadValidatorTests

```csharp
using FluentValidation.TestHelper;
using UploadPoc.Application.Commands;
using UploadPoc.Application.Validators;

namespace UploadPoc.Tests.Application;

public class RegisterUploadValidatorTests
{
    private readonly RegisterUploadValidator _validator = new();

    [Fact]
    public void Validate_ValidCommand_ShouldPass()
    {
        var command = new RegisterUploadCommand("test.zip", 1024, "tus", "sha256hash");
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyFileName_ShouldFail()
    {
        var command = new RegisterUploadCommand("", 1024, "tus", "sha256hash");
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(c => c.FileName);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("s3")]
    [InlineData("")]
    public void Validate_InvalidProvider_ShouldFail(string provider)
    {
        var command = new RegisterUploadCommand("test.zip", 1024, provider, "sha256hash");
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(c => c.Provider);
    }
}
```

## Critérios de Sucesso

- Projeto `UploadPoc.Tests` compila sem erros
- `dotnet test` executa com sucesso
- Mínimo 20 testes unitários
- Cobertura das 3 áreas: entity, consumer, validator
- Todos os testes usam Moq para dependências externas
- Testes usam AwesomeAssertions (`.Should()`)
- Nenhum teste depende de banco, fila ou filesystem
- Testes de transição inválida verificam `InvalidOperationException`
- Testes do consumer verificam ambos cenários (match/mismatch)
- Testes do validator cobrem todos os campos obrigatórios
