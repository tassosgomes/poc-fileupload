---
status: pending
parallelizable: true
blocked_by: ["3.0"]
---

<task_context>
<domain>engine/auth</domain>
<type>implementation</type>
<scope>middleware</scope>
<complexity>medium</complexity>
<dependencies>http_server</dependencies>
<unblocks>"6.0", "7.0", "8.0"</unblocks>
</task_context>

# Tarefa 5.0: Autenticação JWT (F01)

## Visão Geral

Implementar autenticação simplificada via JWT para proteger todos os endpoints da API. O login usa credenciais fixas (POC), retorna um token JWT com validade de 8 horas e todos os endpoints de upload, listagem e download rejeitam requisições sem token válido (HTTP 401). Inclui também o `ExceptionHandlingMiddleware` para formato RFC 9457 (Problem Details).

**Importante:** O `tusdotnet` intercepta requisições antes do pipeline de autenticação do ASP.NET Core. Por isso, um `JwtService.ValidateToken()` compartilhado será criado para validação manual no callback do TUS (tarefa 8.0).

## Requisitos

- RF01.1: Endpoint de login com credenciais fixas → JWT válido por 8h.
- RF01.2: Todos os endpoints de upload, listagem e download rejeitam req. sem JWT (HTTP 401).
- RF01.3: Token no header `Authorization: Bearer <token>`.
- Formato de erro: RFC 9457 (Problem Details).

## Subtarefas

- [ ] 5.1 Criar `AuthController` em `1-Services/UploadPoc.API/Controllers/AuthController.cs`:
  - `POST /api/v1/auth/login` — aceita `{ "username": "admin", "password": "admin123" }`
  - Credenciais fixas hardcoded (POC)
  - Retorna `{ "token": "eyJ...", "expiresAt": "ISO8601" }`
  - Retorna 401 para credenciais inválidas (Problem Details)
- [ ] 5.2 Configurar JWT Authentication no `Program.cs`:
  - `AddAuthentication(JwtBearerDefaults.AuthenticationScheme)`
  - `AddJwtBearer` com validação de issuer, audience e signing key (HMAC-SHA256)
  - Chave secreta via `Jwt:Secret` do appsettings/env var (mínimo 32 chars)
  - Issuer/Audience: `upload-poc`
  - Validade: 8 horas
- [ ] 5.3 Criar serviço `JwtService` na camada Application ou em Services:
  - `GenerateToken(string username)` → retorna string JWT
  - `ValidateToken(string token)` → retorna `ClaimsPrincipal?` (para uso no TUS)
  - Injetar via DI como Singleton
- [ ] 5.4 Adicionar `[Authorize]` em todos os controllers exceto `AuthController.Login`
- [ ] 5.5 Criar `ExceptionHandlingMiddleware` em `1-Services/UploadPoc.API/Middleware/ExceptionHandlingMiddleware.cs`:
  - Capturar exceções não tratadas
  - Retornar Problem Details (RFC 9457) com status code apropriado
  - Logar exceções via Serilog
- [ ] 5.6 Configurar Serilog no `Program.cs`:
  - Output JSON no console
  - Campos: `timestamp`, `level`, `message`, `service.name = upload-poc`
- [ ] 5.7 Configurar Health Checks no `Program.cs`:
  - PostgreSQL, RabbitMQ, MinIO
  - Endpoint: `GET /health`
- [ ] 5.8 Testar login via Swagger e validar que endpoints protegidos retornam 401 sem token

## Sequenciamento

- Bloqueado por: 3.0 (PostgreSQL — para Health Checks e middleware completo)
- Desbloqueia: 6.0 (Registro de Metadados), 7.0 (MinIO), 8.0 (TUS)
- Paralelizável: Sim (pode ser feito em paralelo com 4.0 — RabbitMQ)

## Detalhes de Implementação

### AuthController

```csharp
[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly JwtService _jwtService;

    [HttpPost("login")]
    [AllowAnonymous]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        // Credenciais fixas para POC
        if (request.Username != "admin" || request.Password != "admin123")
            return Unauthorized(new ProblemDetails
            {
                Title = "Invalid credentials",
                Status = 401,
                Detail = "Username or password is incorrect"
            });

        var token = _jwtService.GenerateToken(request.Username);
        return Ok(new { token, expiresAt = DateTime.UtcNow.AddHours(8) });
    }
}

public record LoginRequest(string Username, string Password);
```

### Configuração JWT no Program.cs

```csharp
var jwtSecret = builder.Configuration["Jwt:Secret"]!;
var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = "upload-poc",
            ValidAudience = "upload-poc",
            IssuerSigningKey = key
        };
    });

builder.Services.AddAuthorization();
```

### JwtService

```csharp
public class JwtService
{
    private readonly string _secret;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _expirationHours;

    public string GenerateToken(string username)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(_expirationHours),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        var handler = new JwtSecurityTokenHandler();
        try
        {
            return handler.ValidateToken(token, new TokenValidationParameters { ... }, out _);
        }
        catch { return null; }
    }
}
```

### ExceptionHandlingMiddleware

```csharp
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public async Task InvokeAsync(HttpContext context)
    {
        try { await _next(context); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception");
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Title = "Internal Server Error",
                Status = 500,
                Detail = ex.Message
            });
        }
    }
}
```

### Serilog Config

```csharp
builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration)
          .WriteTo.Console(new CompactJsonFormatter())
          .Enrich.WithProperty("service.name", "upload-poc"));
```

## Critérios de Sucesso

- `POST /api/v1/auth/login` com credenciais corretas retorna JWT
- `POST /api/v1/auth/login` com credenciais erradas retorna 401 + Problem Details
- Endpoints protegidos retornam 401 sem token
- Endpoints protegidos retornam 200 com token válido no header `Authorization: Bearer <token>`
- Health check em `GET /health` retorna status dos 3 serviços (PostgreSQL, RabbitMQ, MinIO)
- Logs no console em formato JSON com `service.name = upload-poc`
- Exceções não tratadas retornam Problem Details (RFC 9457)
