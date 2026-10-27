"""
The local stack's broker, built on Moto as infra/env/messaging.tf builds it on AWS, from the same
file: events/broker-subscriptions.json, which BrokerPlan renders from events/subscriptions.json
(BrokerPlanTests fails while the two differ). A topic per producing module; per service a queue, its
dead-letter queue (after five receives) and the dead-letter queue of its subscriptions; and each
subscription with raw delivery and its filter on event_type.

Prints the Events:Broker addresses the hosts read, as environment variables (what Terraform puts in
the task definitions); scripts/local-up.sh keeps them in .local/broker.env. It runs in the Moto image
(which has boto3), against Moto only: it refuses any endpoint that is not on this machine or this
compose network.

Usage: provision.py <broker-subscriptions.json> > broker.env   (BROKER_ENDPOINT=http://moto:5000)
"""

import json
import os
import sys
from urllib.parse import urlparse, urlunparse

import boto3

ACCOUNT_REGION = "us-east-1"
VISIBILITY_TIMEOUT = "60"  # messaging.tf: the slowest handler, with margin
RECEIVE_WAIT = "20"  # long polling
RETENTION = "345600"  # 4 days
DEAD_LETTER_RETENTION = "1209600"  # 14 days
MAX_RECEIVE_COUNT = 5


def local_endpoint(url: str) -> str:
    """An endpoint on this machine or a compose service name; never AWS."""
    host = urlparse(url).hostname or ""
    if host in ("localhost", "127.0.0.1") or (host and "." not in host):
        return url
    sys.exit(f"provision.py: {url} is not a local endpoint; the local stack never calls AWS.")


def main() -> None:
    plan_path = sys.argv[1]
    endpoint = local_endpoint(os.environ.get("BROKER_ENDPOINT", "http://moto:5000"))
    prefix = os.environ.get("BROKER_PREFIX", "hicleanarch-local")
    with open(plan_path, encoding="utf-8") as f:
        plan = json.load(f)

    session = boto3.session.Session(aws_access_key_id="test", aws_secret_access_key="test", region_name=ACCOUNT_REGION)
    sns = session.client("sns", endpoint_url=endpoint)
    sqs = session.client("sqs", endpoint_url=endpoint)
    base = urlparse(endpoint)

    def queue(name: str, attributes: dict) -> tuple[str, str]:
        url = sqs.create_queue(QueueName=name, Attributes=attributes)["QueueUrl"]
        # Moto answers with the host it was asked on; the hosts reach it by the compose name.
        url = urlunparse(urlparse(url)._replace(scheme=base.scheme, netloc=base.netloc))
        arn = sqs.get_queue_attributes(QueueUrl=url, AttributeNames=["QueueArn"])["Attributes"]["QueueArn"]
        return url, arn

    topics = {t: sns.create_topic(Name=f"{prefix}-{t}")["TopicArn"] for t in plan["topics"]}

    queues, subscription_dead_letters = {}, {}
    for q in plan["queues"]:
        _, dlq_arn = queue(f"{prefix}-{q}-dlq", {"MessageRetentionPeriod": DEAD_LETTER_RETENTION})
        queues[q] = queue(f"{prefix}-{q}", {
            "MessageRetentionPeriod": RETENTION,
            "VisibilityTimeout": VISIBILITY_TIMEOUT,
            "ReceiveMessageWaitTimeSeconds": RECEIVE_WAIT,
            "RedrivePolicy": json.dumps({"deadLetterTargetArn": dlq_arn, "maxReceiveCount": MAX_RECEIVE_COUNT}),
        })
        _, subscription_dead_letters[q] = queue(f"{prefix}-{q}-subscription-dlq", {"MessageRetentionPeriod": DEAD_LETTER_RETENTION})

    for s in plan["subscriptions"]:
        topic_arn, queue_arn = topics[s["topic"]], queues[s["queue"]][1]
        attributes = {
            "RawMessageDelivery": "true",
            "FilterPolicyScope": "MessageAttributes",
            "FilterPolicy": s["filter_policy"],
            "RedrivePolicy": json.dumps({"deadLetterTargetArn": subscription_dead_letters[s["queue"]]}),
        }
        existing = [x["SubscriptionArn"] for x in sns.list_subscriptions_by_topic(TopicArn=topic_arn)["Subscriptions"]
                    if x["Protocol"] == "sqs" and x["Endpoint"] == queue_arn]
        if existing:  # a second run changes the subscription in place, as Terraform would
            for name, value in attributes.items():
                sns.set_subscription_attributes(SubscriptionArn=existing[0], AttributeName=name, AttributeValue=value)
        else:
            sns.subscribe(TopicArn=topic_arn, Protocol="sqs", Endpoint=queue_arn, Attributes=attributes)
        print(f"  {s['topic']} -> {s['queue']}: {s['filter_policy']}", file=sys.stderr)

    lines = [f"Events__Broker__ServiceUrl={endpoint}", f"Events__Broker__Region={ACCOUNT_REGION}"]
    lines += [f"Events__Broker__Topics__{t}={arn}" for t, arn in topics.items()]
    for q, (url, arn) in queues.items():
        lines += [f"Events__Broker__Queues__{q}__Arn={arn}", f"Events__Broker__Queues__{q}__Url={url}"]
    print("# Written by local/broker/provision.py (scripts/local-up.sh). Do not edit.\n" + "\n".join(lines))
    print(f"{len(topics)} topics, {len(queues)} queues, {len(plan['subscriptions'])} subscriptions on {endpoint}", file=sys.stderr)


if __name__ == "__main__":
    main()
