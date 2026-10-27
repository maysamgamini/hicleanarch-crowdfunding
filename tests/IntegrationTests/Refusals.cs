using System.Collections.Concurrent;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntegrationTests;

/// <summary>Hosts that must refuse to start, and what they said about why.</summary>
public static class Refusals
{
    /// <summary>
    /// Starts a host that must refuse to start, and returns all it said about why: the exception the
    /// start threw and every error it logged. The log is needed: when a program fails to start, it
    /// disposes its host, and if that happens before WebApplicationFactory asks the host for its
    /// lifetime (a matter of thread scheduling on a loaded machine), the factory throws an
    /// ObjectDisposedException in place of the reason. The host logs the reason before either.
    /// </summary>
    public static string Of<TEntryPoint>(WebApplicationFactory<TEntryPoint> host)
        where TEntryPoint : class
    {
        var log = new ErrorLog();
        using var logged = host.WithWebHostBuilder(web => web.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(log)));

        var failure = Assert.ThrowsAny<Exception>(() => logged.Server);

        return failure + "\n" + log;
    }

    /// <summary>Every warning and error a host logs, with its exception, as text.</summary>
    public sealed class ErrorLog : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public IReadOnlyList<string> Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        public override string ToString() => string.Join("\n", _entries);

        private sealed class Logger(ErrorLog log, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    log._entries.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
                }
            }
        }
    }
}
