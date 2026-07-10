using FluentValidation.TestHelper;
using Microsoft.Extensions.Time.Testing;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Application.Validation;

namespace TicketingPlatform.UnitTests.Validation;

public class UpdateEventRequestValidatorTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private UpdateEventRequestValidator Validator() => new(_clock);

    [Fact]
    public void StartsAt_InThePast_Fails()
    {
        var req = new UpdateEventRequest("Gig", null, null, _clock.GetUtcNow().AddDays(-1));
        Validator().TestValidate(req).ShouldHaveValidationErrorFor(x => x.StartsAt);
    }

    [Fact]
    public void ValidRequest_Passes()
    {
        var req = new UpdateEventRequest("Gig", "Description", "Venue", _clock.GetUtcNow().AddDays(1));
        Validator().TestValidate(req).ShouldNotHaveAnyValidationErrors();
    }
}
