using CrowdFunding.Gateway;

var builder = WebApplication.CreateBuilder(args);

// Every route and the monolith's address come from configuration ("ReverseProxy" in appsettings.json).
// Moving a module out of the monolith is a new cluster and a route here, and nothing else.
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.Services.AddEdgeRateLimits(builder.Configuration.GetSection(EdgeLimitOptions.Section));

// The load balancer in front adds X-Forwarded-For. It is trusted only from inside the VPC
// (ForwardedHeaders:KnownNetwork, its CIDR); from anywhere else a client could write it itself.
builder.Services.AddTrustedProxies(builder.Configuration);

var app = builder.Build();

app.UseForwardedHeaders();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// The limit is part of the proxy pipeline: it runs for every request a route forwards, matched
// however the path is spelled, and for nothing else. With a custom pipeline YARP's own steps are
// added back by hand, in their usual order.
app.MapReverseProxy(proxy =>
{
    proxy.UseMiddleware<EdgeRateLimitMiddleware>();
    proxy.UseSessionAffinity();
    proxy.UseLoadBalancing();
    proxy.UsePassiveHealthChecks();
});

await app.RunAsync();

/// <summary>Visible to the tests, which start the gateway in memory.</summary>
public partial class Program;
