using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;

using CrowdFunding.Messaging;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace CrowdFunding.Testing;

/// <summary>
/// One Moto server (Apache 2.0) emulating SNS and SQS together, so a publish travels through a real
/// SNS subscription (filter policy, raw delivery) into a real SQS queue (receive counts, dead letters).
/// LocalStack is not used: its current releases need an account token.
/// </summary>
public sealed class MotoBroker : IAsyncDisposable
{
    private const int Port = 5000;

    private readonly IContainer _container = new ContainerBuilder("motoserver/moto:5.2.3")
        .WithPortBinding(Port, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(Port).ForPath("/moto-api/")))
        .Build();

    public string ServiceUrl => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(Port)}";

    private static BasicAWSCredentials Credentials => new("test", "test");

    public Task Start() => _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public IAmazonSQS Sqs() => new AmazonSQSClient(Credentials, new AmazonSQSConfig { ServiceURL = ServiceUrl, AuthenticationRegion = "us-east-1" });

    public IAmazonSimpleNotificationService Sns() =>
        new AmazonSimpleNotificationServiceClient(Credentials, new AmazonSimpleNotificationServiceConfig { ServiceURL = ServiceUrl, AuthenticationRegion = "us-east-1" });

    /// <summary>Creates a queue (with a dead-letter queue after <paramref name="maxReceiveCount"/> receives, if given); returns its URL and ARN.</summary>
    public async Task<(string Url, string Arn)> CreateQueue(IAmazonSQS sqs, string name, string? deadLetterArn = null, int maxReceiveCount = 5)
    {
        var attributes = new Dictionary<string, string> { ["VisibilityTimeout"] = "60" };
        if (deadLetterArn is not null)
        {
            attributes["RedrivePolicy"] = $"{{\"maxReceiveCount\":\"{maxReceiveCount}\",\"deadLetterTargetArn\":\"{deadLetterArn}\"}}";
        }

        var created = await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = attributes });
        var url = ServiceUrl + new Uri(created.QueueUrl).AbsolutePath;
        var arn = (await sqs.GetQueueAttributesAsync(url, ["QueueArn"])).QueueARN;
        return (url, arn);
    }

    /// <summary>Subscribes a queue to a topic with raw delivery and, if given, a filter policy.</summary>
    public static async Task Subscribe(IAmazonSimpleNotificationService sns, string topicArn, string queueArn, string? filterPolicy)
    {
        var attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" };
        if (filterPolicy is not null)
        {
            attributes["FilterPolicy"] = filterPolicy;
        }

        await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, Attributes = attributes });
    }

    /// <summary>
    /// What OpenTofu builds from the plan, under a fresh <paramref name="prefix"/>: a topic per producing
    /// module, a queue and a dead-letter queue per service, the plan's filtered subscriptions. Returns the
    /// <c>Events:Broker</c> settings for a host that reads <paramref name="read"/>, and the dead-letter queues' URLs.
    /// </summary>
    public async Task<Provisioned> Provision(BrokerPlan plan, string prefix, IEnumerable<string> read, int maxReceiveCount = 5)
    {
        using var sqs = Sqs();
        using var sns = Sns();
        var settings = new Dictionary<string, string?> { ["Events:Broker:ServiceUrl"] = ServiceUrl };
        var topics = new Dictionary<string, string>();
        foreach (var producer in plan.Topics)
        {
            topics[producer] = (await sns.CreateTopicAsync($"{prefix}-{producer.ToLowerInvariant()}")).TopicArn;
            settings[$"Events:Broker:Topics:{producer}"] = topics[producer];
        }

        var queues = new Dictionary<string, string>();
        var deadLetters = new Dictionary<string, string>();
        foreach (var service in plan.Services.Keys)
        {
            var dlq = await CreateQueue(sqs, $"{prefix}-{service}-dlq");
            var queue = await CreateQueue(sqs, $"{prefix}-{service}", dlq.Arn, maxReceiveCount);
            queues[service] = queue.Arn;
            deadLetters[service] = dlq.Url;
            settings[$"Events:Broker:Queues:{service}:Arn"] = queue.Arn;
            settings[$"Events:Broker:Queues:{service}:Url"] = queue.Url;
        }

        foreach (var subscription in plan.Subscriptions)
        {
            await Subscribe(sns, topics[subscription.Topic], queues[subscription.Queue], subscription.FilterPolicy);
        }

        var i = 0;
        foreach (var service in read)
        {
            settings[$"Events:Broker:Read:{i++}"] = service;
        }

        return new Provisioned(settings, topics, deadLetters);
    }

    public sealed record Provisioned(
        IReadOnlyDictionary<string, string?> Settings,
        IReadOnlyDictionary<string, string> Topics,
        IReadOnlyDictionary<string, string> DeadLetterQueues);
}
