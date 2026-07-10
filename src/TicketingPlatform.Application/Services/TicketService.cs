using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Services;

public sealed class TicketService
{
    private readonly IOrderRepository _orders;
    private readonly TimeProvider _clock;

    public TicketService(IOrderRepository orders, TimeProvider clock)
    {
        _orders = orders;
        _clock = clock;
    }

    public async Task<Result<TicketValidationResponse>> ValidateAsync(string code, CancellationToken ct)
    {
        var ticket = await _orders.GetTicketByCodeForUpdateAsync(code.Trim(), ct);
        if (ticket is null)
            return Result<TicketValidationResponse>.NotFound("Ticket was not found.");

        if (ticket.Status != TicketStatus.Issued)
            return Result<TicketValidationResponse>.Conflict($"Ticket is '{ticket.Status}' and cannot be scanned.");

        ticket.MarkScanned(_clock.GetUtcNow());
        await _orders.SaveChangesAsync(ct);
        TicketingMetrics.TicketsScanned.Add(1);

        return Result<TicketValidationResponse>.Success(new TicketValidationResponse(
            ticket.Id,
            ticket.OrderId,
            ticket.Status.ToString(),
            ticket.ScannedAt));
    }
}
