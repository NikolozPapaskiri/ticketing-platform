namespace TicketingPlatform.Api.Contracts;

// Tenant
public record CreateTenantRequest(string Name, string Slug);
public record TenantResponse(Guid Id, string Name, string Slug);

// Event
public record CreateEventRequest(string Name, string? Description, string? VenueName, DateTimeOffset StartsAt);
public record EventListItemResponse(Guid Id, string Name, string? VenueName, DateTimeOffset StartsAt, string Status);
public record EventResponse(
    Guid Id,
    string Name,
    string? Description,
    string? VenueName,
    DateTimeOffset StartsAt,
    string Status,
    IReadOnlyList<TicketTypeResponse> TicketTypes);

// Ticket type
public record CreateTicketTypeRequest(string Name, decimal Price, string Currency, int TotalQuantity);
public record TicketTypeResponse(
    Guid Id,
    string Name,
    decimal Price,
    string Currency,
    int TotalQuantity,
    int AvailableQuantity);

public record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int PageNumber,
    int PageSize,
    int TotalItems,
    int TotalPages);
