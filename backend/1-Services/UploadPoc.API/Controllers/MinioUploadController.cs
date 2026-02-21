using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UploadPoc.Application.Commands;
using UploadPoc.Application.Dtos;
using UploadPoc.Application.Handlers;

namespace UploadPoc.API.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/uploads/minio")]
public sealed class MinioUploadController : ControllerBase
{
    private readonly InitiateMinioUploadHandler _initiateMinioUploadHandler;
    private readonly CompleteUploadHandler _completeUploadHandler;
    private readonly CancelUploadHandler _cancelUploadHandler;
    private readonly IValidator<CompleteMinioRequest> _completeMinioValidator;

    public MinioUploadController(
        InitiateMinioUploadHandler initiateMinioUploadHandler,
        CompleteUploadHandler completeUploadHandler,
        CancelUploadHandler cancelUploadHandler,
        IValidator<CompleteMinioRequest> completeMinioValidator)
    {
        _initiateMinioUploadHandler = initiateMinioUploadHandler;
        _completeUploadHandler = completeUploadHandler;
        _cancelUploadHandler = cancelUploadHandler;
        _completeMinioValidator = completeMinioValidator;
    }

    [HttpPost("initiate")]
    [ProducesResponseType(typeof(InitiateMinioResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Initiate([FromBody] InitiateMinioRequest request, CancellationToken cancellationToken)
    {
        var username = User.Identity?.Name ?? "unknown";

        var command = new InitiateMinioUploadCommand(
            request.FileName,
            request.FileSizeBytes,
            request.ContentType,
            request.ExpectedSha256,
            username);

        var response = await _initiateMinioUploadHandler.HandleAsync(command, cancellationToken);
        return Ok(response);
    }

    [HttpPost("complete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Complete([FromBody] CompleteMinioRequest request, CancellationToken cancellationToken)
    {
        var validationResult = await _completeMinioValidator.ValidateAsync(request, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new ValidationException(validationResult.Errors);
        }

        var command = new CompleteUploadCommand(
            request.UploadId,
            request.Parts.Select(part => new CompleteUploadPart(part.PartNumber, part.ETag)).ToList());

        await _completeUploadHandler.HandleAsync(command, cancellationToken);
        return Ok();
    }

    [HttpDelete("abort")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Abort([FromQuery] Guid uploadId, CancellationToken cancellationToken)
    {
        await _cancelUploadHandler.HandleAsync(new CancelUploadCommand(uploadId), cancellationToken);
        return NoContent();
    }
}

public sealed record InitiateMinioRequest(
    string FileName,
    long FileSizeBytes,
    string ContentType,
    string ExpectedSha256);
