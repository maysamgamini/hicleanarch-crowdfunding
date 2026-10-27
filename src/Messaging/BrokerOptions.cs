namespace CrowdFunding.Messaging;

/// <summary>Configuration section <c>Events:Broker</c>: where the topics and queues are, and how hard to drive them.</summary>
public sealed class BrokerOptions
{
    /// <summary>An endpoint instead of AWS's, for tests (Moto). Empty in every AWS environment.</summary>
    public string? ServiceUrl { get; set; }

    public string Region { get; set; } = "us-east-1";

    /// <summary>The SNS topic of each producing module, by module name.</summary>
    public Dictionary<string, string> Topics { get; set; } = [];

    /// <summary>The SQS queue of each service, by service name (<c>services</c> in <c>events/subscriptions.json</c>).</summary>
    public Dictionary<string, ServiceQueueAddress> Queues { get; set; } = [];

    /// <summary>The services whose queues this host reads.</summary>
    public List<string> Read { get; set; } = [];

    /// <summary>PublishBatch calls one task keeps in flight.</summary>
    public int MaxCallsInFlight { get; set; } = 8;

    /// <summary>Messages one task handles at once, below the ten connections a module's role has per task.</summary>
    public int MaxConcurrentMessages { get; set; } = 4;

    /// <summary>
    /// The queues' visibility timeout, as <c>infra/env/messaging.tf</c> sets it. While a message is in this
    /// task's hands, waiting for a slot or with its handlers running one after another, the reader extends
    /// its visibility by this much every third of it, so no other reader gets it meanwhile. The queue's own
    /// timeout then only bounds how long a message stays hidden after its task died.
    /// </summary>
    public int VisibilityTimeoutSeconds { get; set; } = 60;

    /// <summary>Long polling: how long a receive waits for a message.</summary>
    public int WaitSeconds { get; set; } = 20;

    /// <summary>
    /// How long a failed message stays hidden, by how many times it has been received (the last for every
    /// later receive, until the dead-letter queue takes it): 30 s, 2 min, 10 min and 30 min unless configured.
    /// Null by default because the configuration binder appends to a list it finds filled.
    /// </summary>
    public List<TimeSpan>? Backoff { get; set; }

    internal IReadOnlyList<TimeSpan> Waits => Backoff is { Count: > 0 } configured ? configured
        : [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];

    /// <summary>How often the subscriptions are checked after start.</summary>
    public TimeSpan CheckEvery { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class ServiceQueueAddress
{
    public string Arn { get; set; } = "";

    public string Url { get; set; } = "";
}
