using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UploadPoc.Application.Dtos;
using UploadPoc.Application.Handlers;
using UploadPoc.Application.Queries;

namespace UploadPoc.API.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/files")]
public sealed class FilesController : ControllerBase
{
    private readonly ListUploadsHandler _listUploadsHandler;
    private readonly GetDownloadUrlHandler _getDownloadUrlHandler;

    public FilesController(ListUploadsHandler listUploadsHandler, GetDownloadUrlHandler getDownloadUrlHandler)
    {
        _listUploadsHandler = listUploadsHandler;
        _getDownloadUrlHandler = getDownloadUrlHandler;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<UploadDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var uploads = await _listUploadsHandler.HandleAsync(new ListUploadsQuery(), cancellationToken);
        return Ok(uploads);
    }

    [HttpGet("{id:guid}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var result = await _getDownloadUrlHandler.HandleAsync(new GetDownloadUrlQuery(id), cancellationToken);

        if (result.Scenario.Equals("TUS", StringComparison.OrdinalIgnoreCase))
        {
            return PhysicalFile(
                result.FilePath!,
                result.ContentType,
                result.FileName,
                enableRangeProcessing: true);
        }

        if (result.Scenario.Equals("MINIO", StringComparison.OrdinalIgnoreCase))
        {
            return Redirect(result.PresignedUrl!);
        }

        throw new InvalidOperationException($"Download scenario '{result.Scenario}' is not supported.");
    }
}
