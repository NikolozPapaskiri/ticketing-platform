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

public sealed class CreateCustomerOrderRequestValidator : AbstractValidator<CreateCustomerOrderRequest>
{
    public CreateCustomerOrderRequestValidator()
    {
        RuleFor(x => x.HoldId).NotEmpty();
    }
}

public sealed class ValidateTicketRequestValidator : AbstractValidator<ValidateTicketRequest>
{
    public ValidateTicketRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(64);
    }
}
