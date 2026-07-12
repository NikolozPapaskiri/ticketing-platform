namespace TicketingPlatform.Api.Common;

/// <summary>
/// Trusted reverse-proxy configuration (section <c>ReverseProxy</c>). When the API runs behind an
/// ingress/load balancer, the socket peer is the proxy - so the real client IP the rate limiter
/// must partition on lives in <c>X-Forwarded-For</c>. That header is attacker-controllable, so it
/// is honoured ONLY when the immediate peer is one of the explicitly trusted proxies/networks
/// here. With <see cref="Enabled"/> false (the default) forwarded headers are ignored entirely and
/// the socket peer IP is used - never blindly trust the header.
/// </summary>
public sealed class ReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    /// <summary>Turn forwarded-header processing on. Off by default - do not trust proxies you did not configure.</summary>
    public bool Enabled { get; init; }

    /// <summary>Exact proxy IPs to trust (e.g. a fixed ingress address).</summary>
    public string[] KnownProxies { get; init; } = [];

    /// <summary>Trusted proxy networks in CIDR form, e.g. <c>10.0.0.0/8</c> (a pod/service CIDR).</summary>
    public string[] KnownNetworks { get; init; } = [];

    /// <summary>How many proxy hops to unwind. Keep this equal to the number of trusted proxies in front.</summary>
    public int ForwardLimit { get; init; } = 1;

    public bool HasTrustedProxies => KnownProxies.Length > 0 || KnownNetworks.Length > 0;
}
