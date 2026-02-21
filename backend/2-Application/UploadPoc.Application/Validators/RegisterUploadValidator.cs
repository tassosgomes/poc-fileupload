using FluentValidation;
using UploadPoc.Application.Commands;

namespace UploadPoc.Application.Validators;

public sealed class RegisterUploadValidator : AbstractValidator<RegisterUploadCommand>
{
    private static readonly string[] AllowedScenarios = ["TUS", "MINIO"];
    private const long MaxFileSizeBytes = 250L * 1024 * 1024 * 1024;

    public RegisterUploadValidator()
    {
        RuleFor(command => command.FileName)
            .NotEmpty()
            .MaximumLength(500);

        RuleFor(command => command.FileSizeBytes)
            .GreaterThan(0)
            .LessThanOrEqualTo(MaxFileSizeBytes)
            .WithMessage("File size must not exceed 250 GB.");

        RuleFor(command => command.ContentType)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(command => command.ExpectedSha256)
            .NotEmpty()
            .Length(64)
            .Matches("^[a-fA-F0-9]{64}$");

        RuleFor(command => command.UploadScenario)
            .NotEmpty()
            .Must(scenario => !string.IsNullOrWhiteSpace(scenario) && AllowedScenarios.Contains(scenario.ToUpperInvariant()))
            .WithMessage("Upload scenario must be TUS or MINIO.");
    }
}
