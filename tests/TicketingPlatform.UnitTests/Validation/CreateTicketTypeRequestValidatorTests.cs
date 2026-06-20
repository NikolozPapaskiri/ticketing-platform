using FluentValidation.TestHelper;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Application.Validation;

namespace TicketingPlatform.UnitTests.Validation;

public class CreateTicketTypeRequestValidatorTests
{
    private readonly CreateTicketTypeRequestValidator _validator = new();
    private static CreateTicketTypeRequest Valid() => new("General", 49.90m, "USD", 100);

    [Fact]
    public void Valid_Passes() =>
        _validator.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Name_Empty_Fails() =>
        _validator.TestValidate(Valid() with { Name = "" }).ShouldHaveValidationErrorFor(x => x.Name);

    [Fact]
    public void Price_Negative_Fails() =>
        _validator.TestValidate(Valid() with { Price = -1m }).ShouldHaveValidationErrorFor(x => x.Price);

    [Fact]
    public void Price_Zero_Passes() =>                 // free tickets allowed by the >= 0 rule
        _validator.TestValidate(Valid() with { Price = 0m }).ShouldNotHaveValidationErrorFor(x => x.Price);

    [Theory]
    [InlineData("us")]      // too short
    [InlineData("usd")]     // lowercase
    [InlineData("USDD")]    // too long
    [InlineData("")]        // empty
    public void Currency_Invalid_Fails(string currency) =>
        _validator.TestValidate(Valid() with { Currency = currency }).ShouldHaveValidationErrorFor(x => x.Currency);

    [Fact]
    public void TotalQuantity_Zero_Fails() =>
        _validator.TestValidate(Valid() with { TotalQuantity = 0 }).ShouldHaveValidationErrorFor(x => x.TotalQuantity);
}
