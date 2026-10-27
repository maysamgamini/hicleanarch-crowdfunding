using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

[assembly: AssemblyFixture(typeof(Messaging.Tests.Moto))]

namespace Messaging.Tests;

/// <summary>
/// One Moto server (Apache 2.0) emulating SNS and SQS together, so a publish travels through a real
/// SNS subscription (filter policy, raw delivery) into a real SQS queue (receive counts, dead letters).
/// LocalStack is not used: its current releases need an account token.
/// </summary>
public sealed class Moto : IAsyncLifetime
{
    private const int Port = 5000;

    private readonly IContainer _container = new ContainerBuilder("motoserver/moto:5.2.3")
        .WithPortBinding(Port, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(Port).ForPath("/moto-api/")))
        .Build();

    private string ServiceUrl => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(Port)}";

    private static BasicAWSCredentials Credentials => new("test", "test");

    public IAmazonSQS Sqs() => new AmazonSQSClient(Credentials, new AmazonSQSConfig { ServiceURL = ServiceUrl, AuthenticationRegion = "us-east-1" });

    public IAmazonSimpleNotificationService Sns() =>
        new AmazonSimpleNotificationServiceClient(Credentials, new AmazonSimpleNotificationServiceConfig { ServiceURL = ServiceUrl, AuthenticationRegion = "us-east-1" });

    /// <summary>Creates a queue (with a dead-letter queue after <paramref name="maxReceiveCount"/> receives, if given); returns its URL and ARN.</summary>
    public async Task<(string Url, string Arn)> CreateQueue(IAmazonSQS sqs, string name, string? deadLetterArn = null, int maxReceiveCount = 5)
    {
        var attributes = new Dictionary<string, string> { ["VisibilityTimeout"] = "0" };
        if (deadLetterArn is not null)
        {
            attributes["RedrivePolicy"] = $"{{\"maxReceiveCount\":\"{maxReceiveCount}\",\"deadLetterTargetArn\":\"{deadLetterArn}\"}}";
        }

        var created = await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = attributes });
        var url = ServiceUrl + new Uri(created.QueueUrl).AbsolutePath;
        var arn = (await sqs.GetQueueAttributesAsync(url, ["QueueArn"])).QueueARN;
        return (url, arn);
    }

    /// <summary>Subscribes a queue to a topic with raw delivery and, if given, a filter on <c>event_type</c>.</summary>
    public static async Task Subscribe(IAmazonSimpleNotificationService sns, string topicArn, string queueArn, params string[] eventTypes)
    {
        var attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" };
        if (eventTypes.Length > 0)
        {
            attributes["FilterPolicy"] = $"{{\"event_type\":[{string.Join(",", eventTypes.Select(t => $"\"{t}\""))}]}}";
        }

        await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, Attributes = attributes });
    }

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
