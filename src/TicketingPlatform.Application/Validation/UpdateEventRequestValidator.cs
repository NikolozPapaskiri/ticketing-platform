using FluentValidation;
using TicketingPlatform.Application.Contracts;

namespace TicketingPlatform.Application.Validation;

public sealed class UpdateEventRequestValidator : AbstractValidator<UpdateEventRequest>
{
    public UpdateEventRequestValidator(TimeProvider clock)
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.VenueName).MaximumLength(200);

        RuleFor(x => x.StartsAt)
            .Must(starts => starts > clock.GetUtcNow())
            .WithMessage("Event start must be in the future.");
    }
}
