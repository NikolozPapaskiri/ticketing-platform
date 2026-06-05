using FluentValidation.TestHelper;
using TicketingPlatform.Api.Contracts;
using TicketingPlatform.Api.Validation;

namespace TicketingPlatform.UnitTests.Validation;

public class CreateTenantRequestValidatorTests
{
    private readonly CreateTenantRequestValidator _validator = new();
    private static CreateTenantRequest Valid() => new("Aurora Live", "aurora-live");

    [Fact]
    public void Valid_Passes() =>
        _validator.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Name_Empty_Fails() =>
        _validator.TestValidate(Valid() with { Name = "" }).ShouldHaveValidationErrorFor(x => x.Name);

    [Theory]
    [InlineData("Aurora Live")]   // space + uppercase
    [InlineData("aurora_live")]   // underscore
    [InlineData("-aurora")]       // leading hyphen
    [InlineData("aurora-")]       // trailing hyphen
    [InlineData("aurora--live")]  // double hyphen
    [InlineData("")]              // empty
    public void Slug_Invalid_Fails(string slug) =>
        _validator.TestValidate(Valid() with { Slug = slug }).ShouldHaveValidationErrorFor(x => x.Slug);

    [Theory]
    [InlineData("aurora-live")]
    [InlineData("vortex-events-2026")]
    [InlineData("abc")]
    public void Slug_Valid_Passes(string slug) =>
        _validator.TestValidate(Valid() with { Slug = slug }).ShouldNotHaveValidationErrorFor(x => x.Slug);
}
