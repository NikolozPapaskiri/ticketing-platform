namespace TicketingPlatform.Application.Contracts;

// Tenant
public record CreateTenantRequest(string Name, string Slug);
public record TenantResponse(Guid Id, string Name, string Slug);

// Event
public record CreateEventRequest(string Name, string? Description, string? VenueName, DateTimeOffset StartsAt);
public record UpdateEventRequest(string Name, string? Description, string? VenueName, DateTimeOffset StartsAt);
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

// Auth
public record RegisterRequest(string Email, string Password);
public record RegisterStaffRequest(string Email, string Password, string Role, Guid? TenantId);
public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);
public record AuthResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken);
public record UserResponse(Guid Id, string Email, string Role, Guid? TenantId);

// Hold
public record CreateHoldRequest(Guid TicketTypeId, int Quantity);
public record HoldResponse(
    Guid Id,
    Guid TicketTypeId,
    int Quantity,
    string Status,
    DateTimeOffset ExpiresAt);

// Order (the booking saga)
public record CreateOrderRequest(Guid HoldId, string CustomerEmail);
public record CreateCustomerOrderRequest(Guid HoldId);
public record OrderResponse(
    Guid Id,
    Guid HoldId,
    string CustomerEmail,
    decimal Amount,
    string Currency,
    string Status);
public record RefundOrderRequest(string? Reason);

// Availability read model (CQRS query side)
public record TicketAvailabilityResponse(
    Guid TicketTypeId,
    string TicketTypeName,
    int Available,
    int Total,
    DateTimeOffset UpdatedAt);

// Public catalog
public record PublicEventListItemResponse(Guid Id, string Name, string? VenueName, DateTimeOffset StartsAt);
public record PublicEventResponse(
    Guid Id,
    string Name,
    string? Description,
    string? VenueName,
    DateTimeOffset StartsAt,
    IReadOnlyList<TicketTypeResponse> TicketTypes);

// Ticket validation
public record ValidateTicketRequest(string Code);
public record TicketValidationResponse(
    Guid TicketId,
    Guid OrderId,
    string Status,
    DateTimeOffset? ScannedAt);

public record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int PageNumber,
    int PageSize,
    int TotalItems,
    int TotalPages);
