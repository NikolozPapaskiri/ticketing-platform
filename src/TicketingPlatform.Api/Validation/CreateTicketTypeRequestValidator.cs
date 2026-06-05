using FluentValidation;
using TicketingPlatform.Api.Contracts;

namespace TicketingPlatform.Api.Validation;

public sealed class CreateTicketTypeRequestValidator : AbstractValidator<CreateTicketTypeRequest>
{
    public CreateTicketTypeRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);

        // >= 0 allows free tickets. Change to GreaterThan(0) if free tickets are not allowed.
        RuleFor(x => x.Price).GreaterThanOrEqualTo(0);

        RuleFor(x => x.Currency)
            .NotEmpty()
            .Matches("^[A-Z]{3}$") // Simple regex for 3-letter uppercase codes. For more robust validation, consider a list of valid ISO currency codes.
            .WithMessage("Currency must be a 3-letter uppercase code, e.g. USD.");

        RuleFor(x => x.TotalQuantity).GreaterThan(0);
    }
}
