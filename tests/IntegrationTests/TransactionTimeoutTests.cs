using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Pledges.Infrastructure.Persistence;

namespace IntegrationTests;

/// <summary>A request-path transaction the database ends at its two-second limit is a 409 try_again, however EF Core wraps it (lesson s3-l05).</summary>
public sealed class TransactionTimeoutTests(Database database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OutboxMessage AnyRow() => new() { Id = Guid.NewGuid(), Type = "Probe", Payload = "{}", OccurredAt = DateTimeOffset.UtcNow };

    private static async Task<(int Status, string? RetryAfter)> Answer(Exception exception)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        var handled = await new DatabaseBusyHandler().TryHandleAsync(http, exception, Ct);
        return handled ? (http.Response.StatusCode, http.Response.Headers.RetryAfter.ToString()) : (500, null);
    }

    private async Task<Exception> AfterTheLimit(Func<PledgesDbContext, Task> next)
    {
        await using var host = database.StartHost();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PledgesDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        db.Add(AnyRow());
        await db.SaveChangesAsync(Ct);
        await Task.Delay(TimeSpan.FromSeconds(3), Ct); // the database ends the session at 2 s
        var thrown = await Record.ExceptionAsync(() => next(db));
        Assert.NotNull(thrown);
        return thrown;
    }

    [Fact]
    public async Task A_save_after_the_limit_is_a_409_try_again()
    {
        var thrown = await AfterTheLimit(db =>
        {
            db.Add(AnyRow());
            return db.SaveChangesAsync(Ct);
        });

        Assert.Equal((StatusCodes.Status409Conflict, "1"), await Answer(thrown));
    }

    public static TheoryData<Exception> Wrapped() => new()
    {
        // What the host logged under load (evidence/code.md, CQA-501): the timeout inside EF Core's
        // save, and what the next statement finds once the session is gone.
        new Microsoft.EntityFrameworkCore.DbUpdateException("An error occurred while saving the entity changes.",
            new Npgsql.PostgresException("terminating connection due to transaction timeout", "FATAL", "FATAL", "25P04")),
        new InvalidOperationException("Transaction is already completed"),
        new ObjectDisposedException(nameof(Npgsql.NpgsqlTransaction)),
    };

    [Theory]
    [MemberData(nameof(Wrapped))]
    public async Task A_timeout_however_it_is_wrapped_is_a_409_try_again(Exception thrown) =>
        Assert.Equal((StatusCodes.Status409Conflict, "1"), await Answer(thrown));

    [Fact]
    public async Task Another_invalid_operation_is_not_mistaken_for_a_timeout() =>
        Assert.Equal((500, (string?)null), await Answer(new InvalidOperationException("Sequence contains no elements")));

    [Fact]
    public async Task A_commit_after_the_limit_is_a_409_try_again()
    {
        var thrown = await AfterTheLimit(db => db.Database.CommitTransactionAsync(Ct));

        Assert.Equal((StatusCodes.Status409Conflict, "1"), await Answer(thrown));
    }
}
