using FluentValidation;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Validation;

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        // Length is the rule that actually matters for PBKDF2-protected passwords;
        // composition rules add little and hurt usability. NIST 800-63B agrees.
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8).MaximumLength(128);
    }
}

public sealed class RegisterStaffRequestValidator : AbstractValidator<RegisterStaffRequest>
{
    public RegisterStaffRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8).MaximumLength(128);

        RuleFor(x => x.Role)
            .Must(r => r is nameof(UserRole.OrganizerStaff) or nameof(UserRole.PlatformAdmin))
            .WithMessage("Role must be 'OrganizerStaff' or 'PlatformAdmin'. Customers register themselves.");

        // Cross-field rules: staff belong to a tenant, admins must not.
        RuleFor(x => x.TenantId)
            .NotNull().When(x => x.Role == nameof(UserRole.OrganizerStaff))
            .WithMessage("OrganizerStaff requires a TenantId.");
        RuleFor(x => x.TenantId)
            .Null().When(x => x.Role == nameof(UserRole.PlatformAdmin))
            .WithMessage("PlatformAdmin must not have a TenantId.");
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}
