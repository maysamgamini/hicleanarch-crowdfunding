using System.Net;
using System.Net.Sockets;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Gateway.Tests;

/// <summary>
/// A real HTTP server on a free port that says who it is and echoes what it received, byte for byte:
/// stands in for the monolith or the Payments service.
/// </summary>
public sealed class EchoService : IAsyncDisposable
{
    private readonly WebApplication _app;

    private EchoService(WebApplication app, string address)
    {
        _app = app;
        Address = address;
    }

    public string Address { get; }

    public static async Task<EchoService> Start(string name)
    {
        var port = FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.Run(async context =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            await context.Response.WriteAsJsonAsync(new
            {
                service = name,
                path = context.Request.Path.Value,
                method = context.Request.Method,
                body = Convert.ToBase64String(body.ToArray()),
                contentType = context.Request.ContentType,
                signature = context.Request.Headers["Psp-Signature"].ToString(),
            });
        });
        await app.StartAsync();
        return new EchoService(app, $"http://127.0.0.1:{port}/");
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
