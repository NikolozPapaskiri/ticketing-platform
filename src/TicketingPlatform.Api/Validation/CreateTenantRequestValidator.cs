using FluentValidation;
using TicketingPlatform.Api.Contracts;

public sealed class CreateTenantRequestValidator : AbstractValidator<CreateTenantRequest>

{
    public CreateTenantRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Slug)
            .NotEmpty()
            .MaximumLength(100)
            .Matches("^[a-z0-9]+(?:-[a-z0-9]+)*$") // Simple regex for slug format: lowercase letters, numbers, and hyphens.
            .WithMessage("Slug must be lowercase letters, numbers, and single hyphens, e.g. 'aurora-live'.");
    }
}
