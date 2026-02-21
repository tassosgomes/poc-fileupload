using FluentValidation;
using UploadPoc.Application.Dtos;

namespace UploadPoc.Application.Validators;

public sealed class CompleteMinioValidator : AbstractValidator<CompleteMinioRequest>
{
    public CompleteMinioValidator()
    {
        RuleFor(request => request.UploadId)
            .NotEmpty();

        RuleFor(request => request.Parts)
            .NotEmpty()
            .Must(parts => parts.Count >= 1);

        RuleForEach(request => request.Parts)
            .ChildRules(part =>
            {
                part.RuleFor(p => p.PartNumber)
                    .GreaterThan(0);

                part.RuleFor(p => p.ETag)
                    .NotEmpty();
            });
    }
}
