using FluentValidation.TestHelper;
using Microsoft.Extensions.Time.Testing;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Application.Validation;

namespace TicketingPlatform.UnitTests.Validation;

public class CreateEventRequestValidatorTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private CreateEventRequestValidator Validator() => new(_clock);

    [Fact]
    public void StartsAt_InThePast_Fails()
    {
        var req = new CreateEventRequest("Gig", null, null, _clock.GetUtcNow().AddDays(-1));
        Validator().TestValidate(req).ShouldHaveValidationErrorFor(x => x.StartsAt);
    }

    [Fact]
    public void StartsAt_InTheFuture_Passes()
    {
        var req = new CreateEventRequest("Gig", null, null, _clock.GetUtcNow().AddDays(1));
        Validator().TestValidate(req).ShouldNotHaveValidationErrorFor(x => x.StartsAt);
    }

    [Fact]
    public void Name_Empty_Fails()
    {
        var req = new CreateEventRequest("", null, null, _clock.GetUtcNow().AddDays(1));
        Validator().TestValidate(req).ShouldHaveValidationErrorFor(x => x.Name);
    }
}
