using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace IntegrationTests;

/// <summary>From a trace id, an X-Ray trace's or a problem reply's, to the log lines of that request.</summary>
public sealed class LoggingTests(Database database)
{
    [Fact]
    public async Task A_line_logged_while_handling_a_request_carries_that_requests_trace_id()
    {
        var console = new ConsoleCopy();
        await using var host = database.StartHost(s => s
            .AddSingleton<ILoggerProvider>(sp => console.Using(sp))
            .AddSingleton<IStartupFilter, LogsInsideTheRequest>());
        var traceId = ActivityTraceId.CreateRandom();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/campaigns/cmp_{Guid.NewGuid():N}");
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");

        await host.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        var line = Assert.Single(console.Lines, l => l.Contains(LogsInsideTheRequest.Message, StringComparison.Ordinal));
        Assert.DoesNotContain('\n', line.TrimEnd()); // one record, one CloudWatch event
        var scope = JsonDocument.Parse(line).RootElement.GetProperty("Scopes")[0];
        Assert.Equal(traceId.ToString(), scope.GetProperty("TraceId").GetString());
        Assert.Equal(16, scope.GetProperty("SpanId").GetString()!.Length);
    }

    /// <summary>Logs one line from inside the request pipeline, as any handler would.</summary>
    private sealed class LogsInsideTheRequest(ILoggerFactory loggers) : IStartupFilter
    {
        public const string Message = "a line from inside the request";

        private static readonly Action<ILogger, Exception?> Log = LoggerMessage.Define(LogLevel.Information, new EventId(1), Message);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            var logger = loggers.CreateLogger<LoggingTests>();
            app.Use((context, rest) =>
            {
                Log(logger, null);
                return rest(context);
            });
            next(app);
        };
    }

    /// <summary>
    /// Writes every record as the host's console does: with the formatter it chose and the scopes the
    /// logger factory keeps, which include the current activity's ids.
    /// </summary>
    private sealed class ConsoleCopy : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider? _scopes;
        private ConsoleFormatter? _formatter;

        public ConcurrentQueue<string> Lines { get; } = new();

        public ConsoleCopy Using(IServiceProvider services)
        {
            var name = services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName ?? ConsoleFormatterNames.Simple;
            _formatter = services.GetServices<ConsoleFormatter>().Single(f => f.Name == name);
            return this;
        }

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public ILogger CreateLogger(string categoryName) => new Copy(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Copy(ConsoleCopy console, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                using var writer = new StringWriter();
                console._formatter!.Write(new LogEntry<TState>(logLevel, category, eventId, state, exception, formatter), console._scopes, writer);
                console.Lines.Enqueue(writer.ToString());
            }
        }
    }
}
