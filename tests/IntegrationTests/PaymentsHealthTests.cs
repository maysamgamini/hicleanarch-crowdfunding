using System.Net;

using Microsoft.AspNetCore.TestHost;

using Payments.Host;

namespace IntegrationTests;

/// <summary>Payments' container health check (CQA-1518): the program's own probe of its readiness.</summary>
public sealed class PaymentsHealthTests(Database database) : IDisposable
{
    private readonly TwoHosts _hosts = new(database);

    public void Dispose() => _hosts.Dispose();

    private sealed class Answers(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class Silent : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task A_ready_payments_service_passes_its_health_check()
    {
        var ready = new Uri(_hosts.Payments.Server.BaseAddress, "/health/ready");

        Assert.Equal(0, await HealthProbe.Run(ready, _hosts.Payments.Server.CreateHandler()));
    }

    [Fact]
    public async Task Not_ready_or_not_answering_fails_the_health_check()
    {
        var ready = new Uri("http://127.0.0.1:8080/health/ready");

        Assert.Equal(1, await HealthProbe.Run(ready, new Answers(HttpStatusCode.ServiceUnavailable)));
        Assert.Equal(1, await HealthProbe.Run(ready, new Silent(), TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void The_container_runs_the_probe_the_program_provides()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CrowdFunding.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        var service = File.ReadAllText(Path.Combine(root, "infra", "services", "payments", "service.tf"));

        Assert.Contains($"""command     = ["CMD", "dotnet", "CrowdFunding.Payments.Host.dll", "{HealthProbe.Command}"]""", service, StringComparison.Ordinal);
    }
}
