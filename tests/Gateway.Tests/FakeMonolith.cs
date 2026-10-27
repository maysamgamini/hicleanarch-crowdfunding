using System.Net;
using System.Net.Sockets;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Gateway.Tests;

/// <summary>A real HTTP server on a free port that echoes what it received, standing in for the monolith.</summary>
public sealed class FakeMonolith : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeMonolith(WebApplication app, string address)
    {
        _app = app;
        Address = address;
    }

    public string Address { get; }

    public static async Task<FakeMonolith> Start()
    {
        var port = FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.Run(context => context.Response.WriteAsJsonAsync(new
        {
            path = context.Request.Path.Value + context.Request.QueryString.Value,
            method = context.Request.Method,
            authorization = context.Request.Headers.Authorization.ToString(),
            forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString(),
            clientAddress = context.Request.Headers["X-Client-Address"].ToString(),
        }));
        await app.StartAsync();
        return new FakeMonolith(app, $"http://127.0.0.1:{port}/");
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
