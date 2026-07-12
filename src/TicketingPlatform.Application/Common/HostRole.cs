namespace TicketingPlatform.Application.Common;

/// <summary>
/// Which responsibilities this process runs. The same image ships every role; a single
/// <c>Hosting:Role</c> setting decides the profile, so the modular monolith can be deployed as
/// independently-scaled API and worker Deployments without splitting the codebase.
/// </summary>
public enum HostRole
{
    /// <summary>API + all background workers in one process. The default: dev, tests, docker-compose.</summary>
    All,

    /// <summary>Serves HTTP (controllers, SignalR, health) and runs NO polling/background workers.</summary>
    Api,

    /// <summary>Runs the background workers (outbox, consumers, expiry, reconciliation, admission) and serves only health.</summary>
    Worker
}

public static class HostRoleExtensions
{
    /// <summary>Serves API traffic - controllers, SignalR, the sales report. True for All and Api.</summary>
    public static bool ServesApi(this HostRole role) => role is HostRole.All or HostRole.Api;

    /// <summary>Runs the background workers. True for All and Worker.</summary>
    public static bool RunsWorkers(this HostRole role) => role is HostRole.All or HostRole.Worker;

    /// <summary>Parses <c>Hosting:Role</c> (case-insensitive); unknown/empty falls back to <see cref="HostRole.All"/>.</summary>
    public static HostRole ParseHostRole(string? value) =>
        Enum.TryParse<HostRole>(value, ignoreCase: true, out var role) ? role : HostRole.All;
}
