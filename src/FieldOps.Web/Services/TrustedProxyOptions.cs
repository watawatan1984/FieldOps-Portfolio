using System.Net;

namespace FieldOps.Web.Services;

public sealed class TrustedProxyOptions
{
    public const string SectionName = "TrustedProxy";
    public const int MaximumForwardLimit = 5;

    public int ForwardLimit { get; set; } = 1;

    public string[] KnownProxies { get; set; } = [];

    public string[] KnownNetworks { get; set; } = [];

    public bool HasTrustedSources => KnownProxies.Length > 0 || KnownNetworks.Length > 0;

    public static bool HasValidForwardLimit(TrustedProxyOptions options) =>
        options.ForwardLimit is >= 1 and <= MaximumForwardLimit;

    public static bool HasValidProxies(TrustedProxyOptions options) =>
        options.KnownProxies is not null &&
        options.KnownProxies.All(value => IPAddress.TryParse(value, out _));

    public static bool HasValidNetworks(TrustedProxyOptions options) =>
        options.KnownNetworks is not null &&
        options.KnownNetworks.All(value => IPNetwork.TryParse(value, out _));
}