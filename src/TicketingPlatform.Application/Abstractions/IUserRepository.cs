using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Abstractions;

public interface IUserRepository
{
    /// <summary>Lookup by normalized (uppercased) email. Users are not tenant-filtered.</summary>
    Task<User?> GetByEmailAsync(string normalizedEmail, CancellationToken ct);
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken ct);
    void Add(User user);
    Task SaveChangesAsync(CancellationToken ct);
}
