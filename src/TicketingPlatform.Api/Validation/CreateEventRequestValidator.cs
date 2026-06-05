using FluentValidation;
using TicketingPlatform.Api.Contracts;

namespace TicketingPlatform.Api.Validation;

public sealed class CreateEventRequestValidator : AbstractValidator<CreateEventRequest>
{
    public CreateEventRequestValidator(TimeProvider clock)
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.VenueName).MaximumLength(200);

        // Computed inside Must so "now" is read at validation time, not validator construction.
        RuleFor(x => x.StartsAt)
            .Must(starts => starts > clock.GetUtcNow())
            .WithMessage("Event start must be in the future.");
    }
}
