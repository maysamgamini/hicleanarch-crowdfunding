# Events between services (Stage 4). One SNS standard topic per producing module; one SQS queue per
# consuming service, subscribed with raw delivery and a filter on event_type. The topics, queues and
# filters come from events/broker-subscriptions.json, which is generated from events/subscriptions.json
# (BrokerPlanTests); the hosts check the same plan at start and every five minutes.

locals {
  broker_plan = jsondecode(file("${path.module}/../../events/broker-subscriptions.json"))

  topics = toset(local.broker_plan.topics)
  queues = toset(local.broker_plan.queues)

  subscriptions = { for s in local.broker_plan.subscriptions : "${s.queue}_from_${s.topic}" => s }

  # How long a received message stays hidden. A message's handlers run one after another (up to three,
  # each one transaction bounded by the 30 s command timeout), and a message may wait for one of the
  # reader's four slots, so no fixed timeout covers the worst case. The reader receives no more messages
  # than it has free slots, and extends each message's visibility by this much every third of it while
  # the message is in hand (Events:Broker:VisibilityTimeoutSeconds, set from here). This value is then
  # the time a message stays hidden after its task died before another task gets it.
  visibility_timeout_seconds = 60
}

resource "aws_sns_topic" "events" {
  for_each          = local.topics
  name              = "${var.project}-sandbox-${each.key}"
  kms_master_key_id = "alias/aws/sns"
}

# A message read five times without being deleted is a poison message: it moves here and an alarm fires.
# Dead-letter queues keep messages longer than their source, so nothing expires before someone looks.
resource "aws_sqs_queue" "dead_letters" {
  for_each                  = local.queues
  name                      = "${var.project}-sandbox-${each.key}-dlq"
  message_retention_seconds = 1209600 # 14 days
  sqs_managed_sse_enabled   = true
}

resource "aws_sqs_queue" "events" {
  for_each                   = local.queues
  name                       = "${var.project}-sandbox-${each.key}"
  message_retention_seconds  = 345600 # 4 days
  visibility_timeout_seconds = local.visibility_timeout_seconds
  receive_wait_time_seconds  = 20 # long polling: fewer empty receives, lower cost
  sqs_managed_sse_enabled    = true
  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.dead_letters[each.key].arn
    maxReceiveCount     = 5
  })
}

# Only its own source queue may move messages into a dead-letter queue.
resource "aws_sqs_queue_redrive_allow_policy" "dead_letters" {
  for_each  = local.queues
  queue_url = aws_sqs_queue.dead_letters[each.key].id
  redrive_allow_policy = jsonencode({
    redrivePermission = "byQueue"
    sourceQueueArns   = [aws_sqs_queue.events[each.key].arn]
  })
}

# Where SNS puts a message it could not deliver to a subscription (the queue was missing or refused it).
resource "aws_sqs_queue" "subscription_dead_letters" {
  for_each                  = local.queues
  name                      = "${var.project}-sandbox-${each.key}-subscription-dlq"
  message_retention_seconds = 1209600
  sqs_managed_sse_enabled   = true
}

locals {
  # The topics that feed each queue: only they may send to it (aws:SourceArn).
  queue_sources = {
    for q in local.queues : q => distinct([for s in local.subscriptions : aws_sns_topic.events[s.topic].arn if s.queue == q])
  }
}

data "aws_iam_policy_document" "queue_from_topics" {
  for_each = local.queues
  statement {
    actions   = ["sqs:SendMessage"]
    resources = [aws_sqs_queue.events[each.key].arn, aws_sqs_queue.subscription_dead_letters[each.key].arn]
    principals {
      type        = "Service"
      identifiers = ["sns.amazonaws.com"]
    }
    condition {
      test     = "ArnEquals"
      variable = "aws:SourceArn"
      values   = local.queue_sources[each.key]
    }
  }
}

resource "aws_sqs_queue_policy" "events" {
  for_each  = local.queues
  queue_url = aws_sqs_queue.events[each.key].id
  policy    = data.aws_iam_policy_document.queue_from_topics[each.key].json
}

resource "aws_sqs_queue_policy" "subscription_dead_letters" {
  for_each  = local.queues
  queue_url = aws_sqs_queue.subscription_dead_letters[each.key].id
  policy    = data.aws_iam_policy_document.queue_from_topics[each.key].json
}

resource "aws_sns_topic_subscription" "events" {
  for_each             = local.subscriptions
  topic_arn            = aws_sns_topic.events[each.value.topic].arn
  protocol             = "sqs"
  endpoint             = aws_sqs_queue.events[each.value.queue].arn
  raw_message_delivery = true
  filter_policy        = each.value.filter_policy
  redrive_policy       = jsonencode({ deadLetterTargetArn = aws_sqs_queue.subscription_dead_letters[each.value.queue].arn })

  depends_on = [aws_sqs_queue_policy.events]
}

# Alarms: any dead letter, a queue falling behind, and SNS failing or filtering out malformed messages.
resource "aws_cloudwatch_metric_alarm" "dead_letters" {
  for_each            = merge({ for q in local.queues : "${q}-dlq" => aws_sqs_queue.dead_letters[q].name }, { for q in local.queues : "${q}-subscription-dlq" => aws_sqs_queue.subscription_dead_letters[q].name })
  alarm_name          = "${var.project}-sandbox-${each.key}"
  alarm_description   = "A message reached ${each.value}: a handler keeps failing on it, or SNS could not deliver it. Inspect, fix, then redrive or replay."
  namespace           = "AWS/SQS"
  metric_name         = "ApproximateNumberOfMessagesVisible"
  dimensions          = { QueueName = each.value }
  statistic           = "Maximum"
  period              = 60
  evaluation_periods  = 1
  comparison_operator = "GreaterThanThreshold"
  threshold           = 0
  treat_missing_data  = "notBreaching"
  alarm_actions       = [aws_sns_topic.alarms.arn]
  ok_actions          = [aws_sns_topic.alarms.arn]
}

resource "aws_cloudwatch_metric_alarm" "queue_age" {
  for_each            = local.queues
  alarm_name          = "${var.project}-sandbox-${each.key}-queue-age"
  alarm_description   = "The oldest message in ${aws_sqs_queue.events[each.key].name} is over 5 minutes old: its readers are down or falling behind."
  namespace           = "AWS/SQS"
  metric_name         = "ApproximateAgeOfOldestMessage"
  dimensions          = { QueueName = aws_sqs_queue.events[each.key].name }
  statistic           = "Maximum"
  period              = 60
  evaluation_periods  = 5
  comparison_operator = "GreaterThanThreshold"
  threshold           = 300
  treat_missing_data  = "notBreaching"
  alarm_actions       = [aws_sns_topic.alarms.arn]
  ok_actions          = [aws_sns_topic.alarms.arn]
}

resource "aws_cloudwatch_metric_alarm" "topic_failures" {
  for_each = merge(
    { for t in local.topics : "${t}-failed" => { topic = t, metric = "NumberOfNotificationsFailed" } },
    { for t in local.topics : "${t}-filtered-invalid" => { topic = t, metric = "NumberOfNotificationsFilteredOut-InvalidAttributes" } },
    { for t in local.topics : "${t}-filtered-no-attributes" => { topic = t, metric = "NumberOfNotificationsFilteredOut-NoMessageAttributes" } },
  )
  alarm_name          = "${var.project}-sandbox-${each.key}"
  alarm_description   = "SNS ${each.value.metric} on ${aws_sns_topic.events[each.value.topic].name}: a delivery failed, or or a message carried no attributes or attributes a filter could not read."
  namespace           = "AWS/SNS"
  metric_name         = each.value.metric
  dimensions          = { TopicName = aws_sns_topic.events[each.value.topic].name }
  statistic           = "Sum"
  period              = 60
  evaluation_periods  = 1
  comparison_operator = "GreaterThanThreshold"
  threshold           = 0
  treat_missing_data  = "notBreaching"
  alarm_actions       = [aws_sns_topic.alarms.arn]
  ok_actions          = [aws_sns_topic.alarms.arn]
}

# Since s4-l10 each service's role names only its own topics and its own queue: the monolith publishes
# the events of the modules it runs and reads its queues; Payments' topic and queue are Payments'
# alone (infra/services/payments). Each producer checks its own topic's subscriptions (SubscriptionCheck).
locals {
  # The modules the monolith runs: its own service's, and Campaigns while it still runs in it (s5-l07).
  monolith_modules = concat(local.broker_plan_services.monolith, local.broker_plan_services.campaigns)
  # The plan's topics of those modules.
  monolith_topics = toset([for m in local.monolith_modules : lower(m) if contains(local.broker_plan.topics, lower(m))])
  # The queues the monolith reads: its own, and Campaigns' (s5-l07); Notifications' left with it at s5-l04.
  monolith_queues      = ["monolith", "campaigns"]
  broker_plan_services = jsondecode(file("${path.module}/../../events/subscriptions.json")).services
}

data "aws_iam_policy_document" "events" {
  statement {
    actions   = ["sns:Publish", "sns:ListSubscriptionsByTopic"]
    resources = [for t in local.monolith_topics : aws_sns_topic.events[t].arn]
  }
  statement {
    # The subscription check reads each subscription's filter; a subscription's ARN is its topic's plus an id.
    actions   = ["sns:GetSubscriptionAttributes"]
    resources = [for t in local.monolith_topics : "${aws_sns_topic.events[t].arn}:*"]
  }
  statement {
    actions   = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:ChangeMessageVisibility"]
    resources = [for q in local.monolith_queues : aws_sqs_queue.events[q].arn]
  }
}

resource "aws_iam_role_policy" "task_events" {
  role   = aws_iam_role.task.id
  policy = data.aws_iam_policy_document.events.json
}

locals {
  # Payments' events, in and out, go through the broker (s4-l02); Payments runs elsewhere (s4-l10). The
  # monolith publishes to its own topics and reads its own queue. Of the payments queue it holds only the
  # ARN: the subscription check looks for its topics' subscriptions to that address, and no role of the
  # monolith grants anything on it. Module names are configuration keys; topics and queues are the
  # plan's lower-case names.
  # Every event Notifications consumes goes through the broker, into the notifications queue (s5-l02),
  # which only the Notifications service reads (s5-l04): the monolith holds nothing of that queue.
  broker_environment = concat(
    [
      { name = "Events__Routes__Payments", value = "Broker" },
      { name = "Events__Producers__Payments__Pledges", value = "Broker" },
      { name = "Events__Producers__Payments__Campaigns", value = "Broker" },
      { name = "Events__Producers__Payments__Notifications", value = "Broker" },
      { name = "Events__Routes__Notifications", value = "Broker" },
      { name = "Events__Broker__Region", value = var.region },
      { name = "Events__Broker__VisibilityTimeoutSeconds", value = tostring(local.visibility_timeout_seconds) },
      { name = "Events__Broker__Queues__monolith__Arn", value = aws_sqs_queue.events["monolith"].arn },
      { name = "Events__Broker__Queues__monolith__Url", value = aws_sqs_queue.events["monolith"].id },
      { name = "Events__Broker__Queues__payments__Arn", value = aws_sqs_queue.events["payments"].arn },
      { name = "Events__Broker__Read__0", value = "monolith" },
      { name = "Events__Broker__Queues__campaigns__Arn", value = aws_sqs_queue.events["campaigns"].arn },
      { name = "Events__Broker__Queues__campaigns__Url", value = aws_sqs_queue.events["campaigns"].id },
    ],
    local.campaigns_broker_routes,
    # The campaigns queue while Campaigns runs here (s5-l07); with campaigns_mode = Remote (s5-l08) only the
    # Campaigns service reads it: a host must not read a queue none of whose modules it runs.
    local.campaigns_here ? [{ name = "Events__Broker__Read__1", value = "campaigns" }] : [],
    [for t in local.monolith_topics : { name = "Events__Broker__Topics__${t}", value = aws_sns_topic.events[t].arn }],
  )
}

output "event_topic_arns" {
  value = { for t, topic in aws_sns_topic.events : t => topic.arn }
}

output "event_queue_urls" {
  value = { for q, queue in aws_sqs_queue.events : q => queue.id }
}
