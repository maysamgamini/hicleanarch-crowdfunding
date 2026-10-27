namespace CrowdFunding.Gateway;

/// <summary>How fast one client may call, shared by every gateway task through Valkey.</summary>
public sealed class EdgeLimitOptions
{
    public const string Section = "EdgeLimits";

    /// <summary>Tokens added to a client's bucket per second.</summary>
    public double PerSecond { get; set; } = 20;

    /// <summary>The bucket's size: how many requests a client may send at once after a pause.</summary>
    public int Burst { get; set; } = 40;

    /// <summary>How many gateway tasks run. Used only when Valkey is down, to split the limit between them.</summary>
    public int GatewayTasks { get; set; } = 2;

    /// <summary>A StackExchange.Redis configuration string for Valkey. Empty: limit in this process only.</summary>
    public string Valkey { get; set; } = "";

    /// <summary>
    /// CloudFront's origin-facing networks, comma-separated (the managed prefix list
    /// com.amazonaws.global.cloudfront.origin-facing). A request from one of them is limited by the
    /// viewer's address in CloudFront-Viewer-Address; from anywhere else that header is ignored.
    /// </summary>
    public string CloudFrontNetworks { get; set; } = "";

    /// <summary>
    /// The PSP's webhook route: not limited per address, because the PSP sends about 250 a second during
    /// settlement from a short published list of addresses, but limited for the whole route.
    /// </summary>
    public RouteLimitOptions Webhook { get; set; } = new();
}

/// <summary>One bucket for every caller of a route, shared by every gateway task.</summary>
public sealed class RouteLimitOptions
{
    /// <summary>Four times the settlement rate of 250 webhooks a second.</summary>
    public double PerSecond { get; set; } = 1000;

    /// <summary>A second's worth.</summary>
    public int Burst { get; set; } = 1000;
}
