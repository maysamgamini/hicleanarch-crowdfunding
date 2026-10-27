using System.Diagnostics;

using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;

using CrowdFunding.Messaging;
using CrowdFunding.Testing;

using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Messaging.Tests;

/// <summary>
/// The trace context crosses SNS and SQS in two message attributes. Spans are collected by an
/// in-memory exporter, as a backend would receive them; Moto plays SNS and SQS.
/// </summary>
public sealed class TraceTests : IDisposable
{
    private const string TestSource = "Messaging.Tests";

    private static readonly ActivitySource Test = new(TestSource);

    private readonly MotoBroker _aws;

    private readonly List<Activity> _spans = [];

    private readonly TracerProvider _tracing;

    public TraceTests(Aws aws)
    {
        _aws = aws.Broker;
        _tracing = Sdk.CreateTracerProviderBuilder()
            .AddSource(TestSource, MessageTraces.ActivitySourceName)
            .AddInMemoryExporter(_spans)
            .Build();
    }

    public void Dispose() => _tracing.Dispose();

    private static string Unique(string name) => $"{name}-{Guid.NewGuid():N}";

    private async Task<(IAmazonSimpleNotificationService Sns, IAmazonSQS Sqs, Publisher Publisher, string QueueUrl)> Broker(string producer)
    {
        var sns = _aws.Sns();
        var sqs = _aws.Sqs();
        var topic = (await sns.CreateTopicAsync(Unique(producer))).TopicArn;
        var queue = await _aws.CreateQueue(sqs, Unique("consumer"));
        await MotoBroker.Subscribe(sns, topic, queue.Arn, null);
        return (sns, sqs, new Publisher(sns, topic), queue.Url);
    }

    /// <summary>Publishes a body with its attributes to one topic, as SnsEventPublisher does.</summary>
    private sealed class Publisher(IAmazonSimpleNotificationService sns, string topic)
    {
        public Task<PublishResponse> Publish(IReadOnlyDictionary<string, string> attributes, CancellationToken ct) =>
            sns.PublishAsync(new PublishRequest
            {
                TopicArn = topic,
                Message = "{}",
                MessageAttributes = attributes.ToDictionary(a => a.Key, a => new Amazon.SimpleNotificationService.Model.MessageAttributeValue { DataType = "String", StringValue = a.Value }),
            }, ct);
    }

    /// <summary>Receives under a receive span, as the reader does, and returns the messages with that span's context.</summary>
    private static async Task<(List<Message> Messages, ActivityContext Received)> Receive(IAmazonSQS sqs, string queueUrl, int expected, CancellationToken ct)
    {
        using var receive = Test.StartActivity("receive consumer", ActivityKind.Consumer, default(ActivityContext));
        var messages = new List<Message>();
        while (messages.Count < expected)
        {
            var batch = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 1,
                MessageAttributeNames = ["All"],
            }, ct)).Messages ?? [];
            messages.AddRange(batch);
        }

        return (messages, receive!.Context);
    }

    [Fact]
    public async Task A_published_event_that_continues_a_pledges_story_is_handled_as_a_child_of_the_change_that_wrote_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sns, sqs, publisher, queueUrl) = await Broker("payments");
        using (sns)
        using (sqs)
        {
            ActivityContext webhook;
            using (var producing = Test.StartActivity("psp webhook setup_succeeded", ActivityKind.Consumer, default(ActivityContext)))
            {
                producing!.TraceStateString = "vendor=1";
                webhook = producing.Context;
                var attributes = EventAttributes.For("PaymentMethodSaved", 1, "payments", producing.Id, producing.TraceStateString);
                await publisher.Publish(attributes, ct);
            }

            var (messages, received) = await Receive(sqs, queueUrl, 1, ct);
            var message = Assert.Single(messages);
            Activity handled;
            using (var span = MessageTraces.StartProcessing("PaymentMethodSaved", "Pledges.ActivatePledge", EventAttributes.Of(message), received, message.MessageId))
            {
                handled = span.Span!;
            }

            var exported = Assert.Single(_spans, s => s.SpanId == handled.SpanId);
            Assert.Equal(webhook.TraceId, exported.TraceId);
            Assert.Equal(webhook.SpanId, exported.ParentSpanId);
            Assert.Equal("vendor=1", exported.TraceStateString);
            Assert.Contains(exported.Links, l => l.Context.SpanId == webhook.SpanId);
            Assert.Contains(exported.Links, l => l.Context.SpanId == received.SpanId);
            Assert.Equal("child", exported.GetTagItem("crowdfunding.trace_shape"));
        }
    }

    [Fact]
    public async Task Charge_requests_written_in_one_close_pass_are_each_handled_in_a_new_trace_linked_to_the_decision()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sns, sqs, publisher, queueUrl) = await Broker("pledges");
        using (sns)
        using (sqs)
        {
            ActivityContext decision;
            using (var close = Test.StartActivity("close and decide", ActivityKind.Internal, default(ActivityContext)))
            {
                decision = close!.Context;
                for (var i = 0; i < 3; i++)
                {
                    await publisher.Publish(EventAttributes.For("PledgeChargeRequested", 1, "pledges", close.Id), ct);
                }
            }

            var (messages, received) = await Receive(sqs, queueUrl, 3, ct);
            var handled = new List<ActivitySpanId>();
            foreach (var message in messages)
            {
                using var span = MessageTraces.StartProcessing("PledgeChargeRequested", "Payments.CreateCharge", EventAttributes.Of(message), received, message.MessageId);
                handled.Add(span.Span!.SpanId);

                Assert.NotEqual(received.TraceId, span.Span.TraceId);
            }

            var exported = _spans.Where(s => handled.Contains(s.SpanId)).ToList();
            Assert.Equal(3, exported.Count);
            Assert.DoesNotContain(_spans, s => s.TraceId == decision.TraceId && s.OperationName.StartsWith("process ", StringComparison.Ordinal));
            Assert.Equal(3, exported.Select(s => s.TraceId).Distinct().Count());
            Assert.All(exported, s =>
            {
                Assert.Equal(default, s.ParentSpanId);
                var link = Assert.Single(s.Links);
                Assert.Equal(decision.TraceId, link.Context.TraceId);
                Assert.Equal(decision.SpanId, link.Context.SpanId);
                Assert.Equal(decision.TraceId.ToString(), s.GetTagItem(MessageTraces.LinkedTraceId));
                Assert.Equal("new_trace", s.GetTagItem("crowdfunding.trace_shape"));
            });
        }
    }

    [Fact]
    public void Only_the_events_of_one_pledges_story_are_children_and_every_fanned_out_event_gets_its_own_trace()
    {
        Assert.Equal(
            ["PaymentMethodSaved", "PledgeChargeFailed", "PledgeChargeSucceeded"],
            MessageTraces.ContinueOneStory.Order(StringComparer.Ordinal));
        Assert.All(
            ["PledgeChargeRequested", "CampaignFundingDecided", "CampaignPublished", "CampaignSettled"],
            type => Assert.DoesNotContain(type, MessageTraces.ContinueOneStory));
    }

    [Fact]
    public void The_trace_context_is_two_of_the_five_attributes_and_counts_toward_the_cap_of_ten()
    {
        var traceParent = $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01";

        var attributes = EventAttributes.For("PaymentMethodSaved", 1, "payments", traceParent, "vendor=1");

        Assert.Equal(5, attributes.Count);
        Assert.Equal(traceParent, attributes[MessageAttributes.TraceParent]);
        Assert.Equal("vendor=1", attributes[MessageAttributes.TraceState]);
        Assert.Equal(3, EventAttributes.For("PaymentMethodSaved", 1, "payments", null).Count);
        Assert.Equal(3, EventAttributes.For("PaymentMethodSaved", 1, "payments", "not a traceparent").Count);

        var eleven = new Dictionary<string, string>(attributes);
        for (var i = 0; i < 6; i++)
        {
            eleven[$"extra{i}"] = "x";
        }

        Assert.Throws<InvalidOperationException>(() => MessageAttributes.Guard(eleven));
        eleven.Remove("extra0");
        MessageAttributes.Guard(eleven);
    }

    [Fact]
    public void A_message_without_a_trace_context_is_handled_in_a_new_trace_with_no_link()
    {
        using var span = MessageTraces.StartProcessing("PaymentMethodSaved", "Pledges.ActivatePledge", new Dictionary<string, string>());

        Assert.Equal(default, span.Span!.ParentSpanId);
        Assert.Empty(span.Span.Links);
    }
}
