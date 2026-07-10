using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Infrastructure.Persistence;
using TicketingPlatform.IntegrationTests.Support;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Dedicated host for the payment-race suite. It swaps the real HTTP payment client for a
/// <see cref="ControllablePaymentGateway"/> and registers a <see cref="FaultInterceptor"/>, so a
/// test can freeze a charge mid-flight or crash the confirmation save on demand. Runs on its own
/// containers (like the expiry suite) with a short hold TTL so the expiry worker can genuinely
/// race an in-flight payment inside a test.
/// </summary>
public class PaymentRaceApiFactory : TicketingApiFactory
{
    public ControllablePaymentGateway Gateway { get; } = new();
    public FaultInterceptor Fault { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Short TTL + fast scan so the hold-expiry worker can expire an Active hold while a
        // payment for it is frozen at the charge barrier. Comfortably above the sub-second
        // barrier-release latency of the non-expiry tests, so it never expires them early.
        builder.UseSetting("Holds:TtlSeconds", "3");
        builder.UseSetting("Holds:ExpiryScanSeconds", "1");

        // Long payment lease so the reconciliation worker never touches a still-in-flight test
        // order (the tests drive recovery explicitly via the client's retry, not the worker).
        builder.UseSetting("Holds:PaymentLeaseSeconds", "600");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPaymentGateway>();
            services.AddSingleton<IPaymentGateway>(Gateway);

            // Attach the fault/coordination interceptor to the DbContext options explicitly.
            // (EF 10 does not reliably auto-discover an IInterceptor from the app container, so
            // we re-register the options with the same Npgsql connection plus AddInterceptors.)
            services.AddDbContext<TicketingDbContext>((sp, options) =>
            {
                options.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Default"));
                options.AddInterceptors(Fault);
            });
        });
    }
}
