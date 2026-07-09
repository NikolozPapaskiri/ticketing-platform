using Microsoft.EntityFrameworkCore;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Infrastructure.Persistence.Repositories;

public sealed class UserRepository : IUserRepository
{
    private readonly TicketingDbContext _db;
    public UserRepository(TicketingDbContext db) => _db = db;

    public Task<User?> GetByEmailAsync(string normalizedEmail, CancellationToken ct) =>
        _db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, ct);

    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct) =>
        _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken ct) =>
        _db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, ct);

    public void Add(User user) => _db.Users.Add(user);

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
