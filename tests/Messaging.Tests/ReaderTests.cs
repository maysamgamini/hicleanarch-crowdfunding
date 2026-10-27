using System.Collections.Concurrent;
using System.Text.Json;

using Amazon.SQS;
using Amazon.SQS.Model;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Events;

using CrowdFunding.Messaging;

using Microsoft.Extensions.Logging.Abstractions;

namespace Messaging.Tests;

/// <summary>The reader's bounds: what it receives at once, and how long a message it holds stays hidden (CQA-1506).</summary>
public sealed class ReaderTests
{
    public sealed record Spoken(Guid EventId, DateTimeOffset OccurredAt) : IntegrationEvent(EventId, OccurredAt);

    private const string QueueUrl = "http://queue/payments";

    /// <summary>A queue in memory: it hands out as many messages as a receive asks for, and records every call.</summary>
    private sealed class FakeQueue
    {
        private int _left;

        public FakeQueue(int messages) => _left = messages;

        public ConcurrentQueue<int> Asked { get; } = new();

        public ConcurrentQueue<(string Receipt, int Seconds, DateTime At)> Extended { get; } = new();

        public ConcurrentQueue<(string Receipt, DateTime At)> Deleted { get; } = new();

        public IAmazonSQS Client(CancellationToken ct) => Fake.Of<IAmazonSQS>((method, args) => method.Name switch
        {
            nameof(IAmazonSQS.ReceiveMessageAsync) => Receive((ReceiveMessageRequest)args[0]!, ct),
            nameof(IAmazonSQS.ChangeMessageVisibilityAsync) => Extend((string)args[1]!, (int)args[2]!),
            nameof(IAmazonSQS.DeleteMessageAsync) => Delete((string)args[1]!),
            nameof(IAmazonSQS.Dispose) => null,
            _ => throw new NotSupportedException(method.Name),
        });

        private async Task<ReceiveMessageResponse> Receive(ReceiveMessageRequest request, CancellationToken ct)
        {
            Asked.Enqueue(request.MaxNumberOfMessages ?? 1);
            var messages = new List<Message>();
            while (messages.Count < (request.MaxNumberOfMessages ?? 1) && Interlocked.Decrement(ref _left) >= 0)
            {
                var spoken = new Spoken(Guid.NewGuid(), DateTimeOffset.UtcNow);
                messages.Add(new Message
                {
                    MessageId = spoken.EventId.ToString(),
                    ReceiptHandle = spoken.EventId.ToString(),
                    Body = JsonSerializer.Serialize(spoken, JsonSerializerOptions.Web),
                    MessageAttributes = new()
                    {
                        [MessageAttributes.EventType] = new() { DataType = "String", StringValue = nameof(Spoken) },
                        [MessageAttributes.EventVersion] = new() { DataType = "String", StringValue = "1" },
                        [MessageAttributes.Producer] = new() { DataType = "String", StringValue = "Campaigns" },
                    },
                });
            }

            if (messages.Count == 0)
            {
                await Task.Delay(50, ct); // a long poll that found nothing
            }

            return new ReceiveMessageResponse { Messages = messages };
        }

        private Task<ChangeMessageVisibilityResponse> Extend(string receipt, int seconds)
        {
            Extended.Enqueue((receipt, seconds, DateTime.UtcNow));
            return Task.FromResult(new ChangeMessageVisibilityResponse());
        }

        private Task<DeleteMessageResponse> Delete(string receipt)
        {
            Deleted.Enqueue((receipt, DateTime.UtcNow));
            return Task.FromResult(new DeleteMessageResponse());
        }
    }

    /// <summary>The monolith of s4-l02 reading the payments queue, where one handler of Payments takes <see cref="Spoken"/>.</summary>
    private static SqsEventReader Reader(IAmazonSQS sqs, BrokerOptions options, Func<CancellationToken, Task> handle)
    {
        var module = Fake.Of<IModuleEvents>((method, args) => method.Name switch
        {
            "get_Module" => "Payments",
            nameof(IModuleEvents.HandleOnce) => ((Func<IServiceProvider, CancellationToken, Task>)args[2]!)(null!, (CancellationToken)args[3]!),
            _ => throw new NotSupportedException(method.Name),
        });
        var handler = new EventHandlerRegistration(typeof(Spoken), "Payments.Slow", "Payments", (_, _, ct) => handle(ct));
        var subscriptions = EventSubscriptions.Load(Path.Combine(AppContext.BaseDirectory, "events", "subscriptions.json"))
            .With([new EventSubscription(nameof(Spoken), "Payments", "Campaigns")]);
        var routes = new EventRoutes(subscriptions, [module], [handler], [], [], Microsoft.Extensions.Options.Options.Create(new EventRoutingOptions()));
        options.Queues["payments"] = new ServiceQueueAddress { Url = QueueUrl, Arn = "arn:payments" };
        options.Read.Add("payments");
        return new SqsEventReader(sqs, options, BrokerPlanTests.Plan, routes, [handler], [], [module], NullLogger<SqsEventReader>.Instance);
    }

    private static async Task Within(TimeSpan limit, Func<bool> condition)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until && !condition())
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task A_receive_asks_for_no_more_messages_than_the_task_can_start_handling()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = new FakeQueue(messages: 12);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        using var reader = Reader(queue.Client(ct), new BrokerOptions { MaxConcurrentMessages = 4 }, async c =>
        {
            Interlocked.Increment(ref running);
            await gate.Task.WaitAsync(c);
        });

        await reader.StartAsync(ct);
        await Within(TimeSpan.FromSeconds(5), () => Volatile.Read(ref running) == 4);
        await Task.Delay(300, ct); // every slot is busy: the reader must not receive more meanwhile

        Assert.Equal([4], queue.Asked);
        gate.SetResult();
        await Within(TimeSpan.FromSeconds(5), () => queue.Deleted.Count == 12);
        await reader.StopAsync(ct);
        Assert.All(queue.Asked, asked => Assert.InRange(asked, 1, 4));
    }

    [Fact]
    public async Task A_message_held_longer_than_a_third_of_the_visibility_timeout_is_kept_hidden_until_it_is_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = new FakeQueue(messages: 1);
        using var reader = Reader(queue.Client(ct), new BrokerOptions { VisibilityTimeoutSeconds = 3 },
            c => Task.Delay(TimeSpan.FromSeconds(2.5), c)); // its handlers together outlast a third of the timeout twice

        await reader.StartAsync(ct);
        await Within(TimeSpan.FromSeconds(10), () => queue.Deleted.Count == 1);
        await Task.Delay(TimeSpan.FromSeconds(1.5), ct); // past another third: no extension after the delete
        await reader.StopAsync(ct);

        var deleted = queue.Deleted.Single();
        Assert.InRange(queue.Extended.Count, 2, 3);
        Assert.All(queue.Extended, e => Assert.Equal((deleted.Receipt, 3), (e.Receipt, e.Seconds)));
        Assert.All(queue.Extended, e => Assert.True(e.At <= deleted.At));
    }
}
