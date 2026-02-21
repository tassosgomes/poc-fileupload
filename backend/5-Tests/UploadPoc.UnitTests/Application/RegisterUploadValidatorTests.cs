using FluentAssertions;
using UploadPoc.Application.Commands;
using UploadPoc.Application.Validators;

namespace UploadPoc.UnitTests.Application;

public class RegisterUploadValidatorTests
{
    private readonly RegisterUploadValidator _validator = new();

    [Fact]
    public void Validate_ValidCommand_ShouldPass()
    {
        var command = CreateValidCommand();

        var result = _validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyFileName_ShouldFail()
    {
        var command = CreateValidCommand() with { FileName = string.Empty };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(RegisterUploadCommand.FileName));
    }

    [Fact]
    public void Validate_NegativeFileSize_ShouldFail()
    {
        var command = CreateValidCommand() with { FileSizeBytes = -1 };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(RegisterUploadCommand.FileSizeBytes));
    }

    [Fact]
    public void Validate_ZeroFileSize_ShouldFail()
    {
        var command = CreateValidCommand() with { FileSizeBytes = 0 };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(RegisterUploadCommand.FileSizeBytes));
    }

    [Fact]
    public void Validate_InvalidProvider_ShouldFail()
    {
        var command = CreateValidCommand() with { UploadScenario = "invalid" };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(RegisterUploadCommand.UploadScenario));
    }

    [Fact]
    public void Validate_FileSizeExceedsLimit_ShouldFail()
    {
        var command = CreateValidCommand() with { FileSizeBytes = 250L * 1024 * 1024 * 1024 + 1 };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(RegisterUploadCommand.FileSizeBytes));
    }

    [Fact]
    public void Validate_FileNameTooLong_ShouldFail()
    {
        var command = CreateValidCommand() with { FileName = new string('a', 501) };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(RegisterUploadCommand.FileName));
    }

    [Fact]
    public void Validate_MissingSha256_ShouldFail()
    {
        var command = CreateValidCommand() with { ExpectedSha256 = string.Empty };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(RegisterUploadCommand.ExpectedSha256));
    }

    [Fact]
    public void Validate_InvalidSha256Format_ShouldFail()
    {
        var command = CreateValidCommand() with { ExpectedSha256 = new string('z', 64) };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(RegisterUploadCommand.ExpectedSha256));
    }

    private static RegisterUploadCommand CreateValidCommand()
    {
        return new RegisterUploadCommand(
            "sample.txt",
            1024,
            "text/plain",
            new string('a', 64),
            "TUS",
            "tester");
    }
}
