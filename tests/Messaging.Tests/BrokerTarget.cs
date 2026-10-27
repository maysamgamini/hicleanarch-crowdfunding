using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;

using CrowdFunding.Messaging;
using CrowdFunding.Testing;

namespace Messaging.Tests;

/// <summary>
/// Where the plan's topics, queues and filter policies are built for a test: Moto by default, or, when
/// <c>BROKER_TESTS_ON_AWS=1</c>, the learner's own AWS account, with the SDK's default credentials and
/// region (<c>AWS_PROFILE</c>, <c>AWS_REGION</c>, ...). Moto is an emulator: its reading of filter
/// policies, raw delivery and the ten-attribute limit is checked against SNS and SQS themselves only
/// there. Everything is built under a fresh prefix, the way Terraform builds it (raw delivery, the
/// event-type filter, a queue policy on <c>aws:SourceArn</c>), and deleted when the test ends. The
/// course's own runs never set the variable: they make no AWS calls.
/// </summary>
public sealed class BrokerTarget : IAsyncDisposable
{
    public const string Variable = "BROKER_TESTS_ON_AWS";

    private readonly List<string> _topics = [];
    private readonly List<string> _queues = [];
    private readonly Func<string, string> _queueUrl;

    private BrokerTarget(IAmazonSimpleNotificationService sns, IAmazonSQS sqs, bool ownAccount, Func<string, string> queueUrl)
    {
        Sns = sns;
        Sqs = sqs;
        OwnAccount = ownAccount;
        _queueUrl = queueUrl;
    }

    public static bool OnAws => Environment.GetEnvironmentVariable(Variable) == "1";

    public IAmazonSimpleNotificationService Sns { get; }

    public IAmazonSQS Sqs { get; }

    /// <summary>True on the learner's AWS account, false on Moto.</summary>
    public bool OwnAccount { get; }

    /// <summary>
    /// How long a subscription's filter policy may take to apply. AWS says changes to a filter policy
    /// can take up to 15 minutes to take full effect; a new subscription applies far sooner in practice,
    /// and a message published before it does is not delivered, so the test waits this long first.
    /// </summary>
    public TimeSpan Settle => OwnAccount ? TimeSpan.FromSeconds(60) : TimeSpan.Zero;

    public static BrokerTarget For(MotoBroker moto) => OnAws
        ? new BrokerTarget(new AmazonSimpleNotificationServiceClient(), new AmazonSQSClient(), true, url => url)
        : new BrokerTarget(moto.Sns(), moto.Sqs(), false, url => moto.ServiceUrl + new Uri(url).AbsolutePath);

    /// <summary>The plan's topics and queues under <paramref name="prefix"/>: topic ARNs by producer, queue URLs by service.</summary>
    public async Task<(Dictionary<string, string> Topics, Dictionary<string, string> Queues)> Build(BrokerPlan plan, string prefix, CancellationToken ct)
    {
        var topics = new Dictionary<string, string>();
        foreach (var producer in plan.Topics)
        {
            topics[producer] = (await Sns.CreateTopicAsync($"{prefix}-{producer.ToLowerInvariant()}", ct)).TopicArn;
            _topics.Add(topics[producer]);
        }

        var queues = new Dictionary<string, string>();
        var arns = new Dictionary<string, string>();
        foreach (var service in plan.Services.Keys)
        {
            var url = _queueUrl((await Sqs.CreateQueueAsync($"{prefix}-{service}", ct)).QueueUrl);
            _queues.Add(url);
            queues[service] = url;
            arns[service] = (await Sqs.GetQueueAttributesAsync(url, ["QueueArn"], ct)).QueueARN;
            var sources = plan.Subscriptions.Where(s => s.Queue == service).Select(s => topics[s.Topic]).ToArray();
            var policy = new
            {
                Version = "2012-10-17",
                Statement = new[]
                {
                    new
                    {
                        Effect = "Allow",
                        Principal = new { Service = "sns.amazonaws.com" },
                        Action = "sqs:SendMessage",
                        Resource = arns[service],
                        Condition = new { ArnEquals = new Dictionary<string, string[]> { ["aws:SourceArn"] = sources } },
                    },
                },
            };
            await Sqs.SetQueueAttributesAsync(url, new Dictionary<string, string>
            {
                ["Policy"] = System.Text.Json.JsonSerializer.Serialize(policy),
            }, ct);
        }

        foreach (var subscription in plan.Subscriptions)
        {
            await Sns.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topics[subscription.Topic],
                Protocol = "sqs",
                Endpoint = arns[subscription.Queue],
                Attributes = new() { ["RawMessageDelivery"] = "true", ["FilterPolicy"] = subscription.FilterPolicy },
            }, ct);
        }

        await Task.Delay(Settle, ct);
        return (topics, queues);
    }

    /// <summary>
    /// Every message the queue holds, deleted as it is read: read until the queue stays empty for a
    /// whole long poll (one second on Moto, ten on AWS, where delivery takes longer), or until
    /// <paramref name="expected"/> have come and one more poll found nothing.
    /// </summary>
    public async Task<List<Message>> ReceiveAll(string queueUrl, int expected, CancellationToken ct)
    {
        var all = new List<Message>();
        var deadline = DateTime.UtcNow + (OwnAccount ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            var page = (await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = OwnAccount ? 10 : 1,
                MessageAttributeNames = ["All"],
            }, ct)).Messages ?? [];
            if (page.Count == 0 && (all.Count >= expected || !OwnAccount))
            {
                return all;
            }

            all.AddRange(page);
            foreach (var message in page)
            {
                await Sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
            }
        }

        return all;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var topic in _topics)
        {
            await Sns.DeleteTopicAsync(topic); // its subscriptions go with it
        }

        foreach (var queue in _queues)
        {
            await Sqs.DeleteQueueAsync(queue);
        }

        Sns.Dispose();
        Sqs.Dispose();
    }
}
