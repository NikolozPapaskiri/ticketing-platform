using TicketingPlatform.Application.Common;

namespace TicketingPlatform.UnitTests.Common;

/// <summary>
/// PR 6 §6.1 - the host-role profile logic that decides which responsibilities a process runs.
/// </summary>
public class HostRoleTests
{
    [Theory]
    [InlineData("Api", HostRole.Api)]
    [InlineData("api", HostRole.Api)]
    [InlineData("Worker", HostRole.Worker)]
    [InlineData("All", HostRole.All)]
    [InlineData(null, HostRole.All)]      // unset falls back to the everything profile
    [InlineData("", HostRole.All)]
    [InlineData("nonsense", HostRole.All)] // unknown never silently drops workers
    public void ParseHostRole_MapsConfigValue(string? value, HostRole expected) =>
        Assert.Equal(expected, HostRoleExtensions.ParseHostRole(value));

    [Theory]
    [InlineData(HostRole.All, true, true)]
    [InlineData(HostRole.Api, true, false)]
    [InlineData(HostRole.Worker, false, true)]
    public void Role_ExposesTheRightResponsibilities(HostRole role, bool servesApi, bool runsWorkers)
    {
        Assert.Equal(servesApi, role.ServesApi());
        Assert.Equal(runsWorkers, role.RunsWorkers());
    }
}
