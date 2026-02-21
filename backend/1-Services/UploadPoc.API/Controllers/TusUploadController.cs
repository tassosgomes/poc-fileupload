using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UploadPoc.Application.Commands;
using UploadPoc.Application.Dtos;
using UploadPoc.Application.Handlers;

namespace UploadPoc.API.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/uploads")]
public sealed class TusUploadController : ControllerBase
{
    private readonly RegisterUploadHandler _registerUploadHandler;
    private readonly CancelUploadHandler _cancelUploadHandler;

    public TusUploadController(RegisterUploadHandler registerUploadHandler, CancelUploadHandler cancelUploadHandler)
    {
        _registerUploadHandler = registerUploadHandler;
        _cancelUploadHandler = cancelUploadHandler;
    }

    [HttpPost("tus/register")]
    [ProducesResponseType(typeof(UploadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Register([FromBody] RegisterTusUploadRequest request, CancellationToken cancellationToken)
    {
        var username = User.Identity?.Name ?? "unknown";

        var command = new RegisterUploadCommand(
            request.FileName,
            request.FileSizeBytes,
            request.ContentType,
            request.ExpectedSha256,
            "TUS",
            username);

        var response = await _registerUploadHandler.HandleAsync(command, cancellationToken);
        return Ok(response);
    }

    [HttpDelete("{id:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        await _cancelUploadHandler.HandleAsync(new CancelUploadCommand(id), cancellationToken);
        return NoContent();
    }
}

public sealed record RegisterTusUploadRequest(
    string FileName,
    long FileSizeBytes,
    string ContentType,
    string ExpectedSha256);
