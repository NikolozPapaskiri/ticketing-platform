using FluentValidation;
using TicketingPlatform.Application.Contracts;

namespace TicketingPlatform.Application.Validation;

public sealed class CreateHoldRequestValidator : AbstractValidator<CreateHoldRequest>
{
    public CreateHoldRequestValidator()
    {
        RuleFor(x => x.TicketTypeId).NotEmpty();

        // Structural bound only. Whether the quantity is actually available is a business
        // decision made against live inventory in HoldService (409), not a validation rule.
        RuleFor(x => x.Quantity).GreaterThan(0);
    }
}
