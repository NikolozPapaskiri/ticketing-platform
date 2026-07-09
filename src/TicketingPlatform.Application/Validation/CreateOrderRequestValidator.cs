using FluentValidation;
using TicketingPlatform.Application.Contracts;

namespace TicketingPlatform.Application.Validation;

public sealed class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    public CreateOrderRequestValidator()
    {
        RuleFor(x => x.HoldId).NotEmpty();
        RuleFor(x => x.CustomerEmail).NotEmpty().EmailAddress().MaximumLength(256);
    }
}
