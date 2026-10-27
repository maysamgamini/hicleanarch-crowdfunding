using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;

using BuildingBlocks.Infrastructure.Events;

using CrowdFunding.Messaging;
using CrowdFunding.Testing;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Messaging.Tests;

public sealed class BrokerTests(Aws aws)
{
    private static string Prefix() => $"t{Guid.NewGuid():N}"[..12];

    private static OutboxRecord Record(string producer, string type) =>
        new(producer, Guid.NewGuid(), type, "{}", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

    [Fact]
    public async Task Every_event_type_reaches_exactly_the_queues_the_subscriptions_file_names()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = BrokerPlanTests.Plan;
        var provisioned = await aws.Broker.Provision(plan, Prefix(), []);
        using var sns = aws.Broker.Sns();
        using var sqs = aws.Broker.Sqs();
        using var publisher = new SnsEventPublisher(sns, new BrokerOptions { Topics = new(provisioned.Topics) });
        var file = EventSubscriptions.Load(Path.Combine(AppContext.BaseDirectory, "events", "subscriptions.json"));

        var records = file.Events.Select(e => Record(e.Value.Producer, e.Key)).ToList();
        Assert.Equal(records.Count, (await publisher.Publish(records, ct)).Count);

        foreach (var (service, modules) in plan.Services)
        {
            var expected = file.Events
                .Where(e => !modules.Contains(e.Value.Producer) && e.Value.Consumers.Any(modules.Contains))
                .Select(e => e.Key).Order(StringComparer.Ordinal);
            var url = provisioned.Settings[$"Events:Broker:Queues:{service}:Url"]!;
            var received = (await ReceiveAll(sqs, url, ct)).Select(m => m.MessageAttributes[MessageAttributes.EventType].StringValue).Order(StringComparer.Ordinal);
            Assert.Equal(expected, received);
        }
    }

    [Fact]
    public void A_message_carries_five_attributes_at_most_and_the_guard_refuses_an_eleventh()
    {
        var attributes = SnsEventPublisher.AttributesOf(Record("Pledges", "PledgeChargeRequested"));
        Assert.Equal([MessageAttributes.EventType, MessageAttributes.EventVersion, MessageAttributes.Producer, MessageAttributes.TraceParent], attributes.Keys);

        MessageAttributes.Guard(Enumerable.Range(1, 10).ToDictionary(i => $"a{i}", _ => "x"));
        Assert.Throws<InvalidOperationException>(() => MessageAttributes.Guard(Enumerable.Range(1, 11).ToDictionary(i => $"a{i}", _ => "x")));
    }

    [Fact]
    public async Task A_commit_of_five_thousand_rows_is_published_ten_to_a_call_with_at_most_eight_calls_in_flight()
    {
        var ct = TestContext.Current.CancellationToken;
        int inFlight = 0, most = 0, largest = 0, calls = 0;
        var sns = Fake.Of<IAmazonSimpleNotificationService>((method, args) =>
        {
            if (method.Name == nameof(IAmazonSimpleNotificationService.Dispose))
            {
                return null;
            }

            var request = (PublishBatchRequest)args[0]!;
            return PublishBatch(request);
        });
        async Task<PublishBatchResponse> PublishBatch(PublishBatchRequest request)
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref most, now);
            InterlockedMax(ref largest, request.PublishBatchRequestEntries.Count);
            Interlocked.Increment(ref calls);
            await Task.Delay(2, ct);
            Interlocked.Decrement(ref inFlight);
            return new PublishBatchResponse { Successful = [.. request.PublishBatchRequestEntries.Select(e => new PublishBatchResultEntry { Id = e.Id })] };
        }

        using var publisher = new SnsEventPublisher(sns, new BrokerOptions { Topics = new() { ["Pledges"] = "arn:pledges" } });
        var records = Enumerable.Range(0, 5000).Select(_ => Record("Pledges", "PledgeChargeRequested")).ToList();

        var accepted = await publisher.Publish(records, ct);

        Assert.Equal(5000, accepted.Count);
        Assert.Equal(500, calls);
        Assert.Equal(10, largest);
        Assert.InRange(most, 1, 8);
    }

    [Fact]
    public async Task A_broker_route_without_its_subscription_stops_the_start()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = BrokerPlanTests.Plan;
        var provisioned = await aws.Broker.Provision(plan, Prefix(), ["payments", "monolith"]);
        using var sns = aws.Broker.Sns();
        var options = Options(provisioned);

        Assert.Empty(await Check(sns, options, plan).Problems(ct));

        // Terraform has not built the Pledges topic's subscription to the payments queue yet.
        var pledges = provisioned.Topics["Pledges"];
        foreach (var subscription in (await sns.ListSubscriptionsByTopicAsync(pledges, ct)).Subscriptions
                     .Where(s => s.Endpoint == options.Queues["payments"].Arn))
        {
            await sns.UnsubscribeAsync(subscription.SubscriptionArn, ct);
        }

        var check = Check(sns, options, plan);
        Assert.Contains(await check.Problems(ct), p => p.Contains("The Pledges topic has no raw-delivery subscription to the payments queue", StringComparison.Ordinal));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => check.StartAsync(ct));
        Assert.Contains("payments queue", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_filter_that_leaves_out_a_routed_event_type_is_a_problem()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = BrokerPlanTests.Plan;
        var provisioned = await aws.Broker.Provision(plan, Prefix(), ["payments", "monolith"]);
        using var sns = aws.Broker.Sns();
        var options = Options(provisioned);
        var subscription = (await sns.ListSubscriptionsByTopicAsync(provisioned.Topics["Payments"], ct)).Subscriptions
            .Single(s => s.Endpoint == options.Queues["monolith"].Arn);
        await sns.SetSubscriptionAttributesAsync(subscription.SubscriptionArn, "FilterPolicy", """{"event_type":["PledgeChargeSucceeded"]}""", ct);

        var problems = await Check(sns, options, plan).Problems(ct);

        Assert.Contains(problems, p => p.Contains("filters out", StringComparison.Ordinal) && p.Contains("PaymentMethodSaved", StringComparison.Ordinal));
    }

    /// <summary>The monolith of s4-l02: every module here, Payments' events in and out through the broker.</summary>
    private static SubscriptionCheck Check(IAmazonSimpleNotificationService sns, BrokerOptions options, BrokerPlan plan)
    {
        string[] modules = ["Campaigns", "Pledges", "Payments", "Notifications"];
        var routing = new EventRoutingOptions
        {
            Routes = new() { ["Payments"] = EventRoute.Broker },
            Producers = new() { ["Payments"] = new() { ["Pledges"] = EventRoute.Broker, ["Campaigns"] = EventRoute.Broker, ["Notifications"] = EventRoute.Broker } },
        };
        var routes = new EventRoutes(
            EventSubscriptions.Load(Path.Combine(AppContext.BaseDirectory, "events", "subscriptions.json")),
            modules.Select(m => Fake.Of<IModuleEvents>((method, _) => method.Name == "get_Module" ? m : throw new NotSupportedException(method.Name))),
            [], [], [], Microsoft.Extensions.Options.Options.Create(routing));
        return new SubscriptionCheck(sns, options, plan, routes, modules.Select(m => new PublishedEvent(typeof(object), m)), NullLogger<SubscriptionCheck>.Instance);
    }

    private static BrokerOptions Options(MotoBroker.Provisioned provisioned)
    {
        var options = new BrokerOptions { Topics = new(provisioned.Topics) };
        foreach (var service in new[] { "payments", "monolith" })
        {
            options.Queues[service] = new ServiceQueueAddress
            {
                Arn = provisioned.Settings[$"Events:Broker:Queues:{service}:Arn"]!,
                Url = provisioned.Settings[$"Events:Broker:Queues:{service}:Url"]!,
            };
            options.Read.Add(service);
        }

        return options;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private static async Task<List<Message>> ReceiveAll(IAmazonSQS sqs, string queueUrl, CancellationToken ct)
    {
        var all = new List<Message>();
        while (true)
        {
            var page = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 1,
                MessageAttributeNames = ["All"],
            }, ct)).Messages ?? [];
            if (page.Count == 0)
            {
                return all;
            }

            all.AddRange(page);
            foreach (var message in page)
            {
                await sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
            }
        }
    }
}
