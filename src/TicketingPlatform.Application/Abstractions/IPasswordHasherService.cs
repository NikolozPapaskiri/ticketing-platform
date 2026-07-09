using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Abstractions;

/// <summary>
/// Port over ASP.NET Core Identity's PasswordHasher (PBKDF2). A port so Application never
/// references the Identity package and unit tests can stub hashing.
/// </summary>
public interface IPasswordHasherService
{
    string Hash(User user, string password);
    bool Verify(User user, string hashedPassword, string providedPassword);
}
