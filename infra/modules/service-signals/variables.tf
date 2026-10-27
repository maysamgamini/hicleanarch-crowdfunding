variable "service" {
  description = "The service's name, as ECS and its log group name it: campaigns, pledges, payments, notifications, gateway."
  type        = string
}

variable "name_prefix" {
  description = "What every alarm and dashboard name starts with, for example hicleanarch-sandbox."
  type        = string
}

variable "region" {
  description = "The Region of the metrics, for the dashboard."
  type        = string
}

variable "alarm_topic_arn" {
  description = "Where a page goes: the composite alarms' actions. The window alarms under them notify nobody."
  type        = string
}

variable "requests" {
  description = <<-EOT
    Where the service's request metrics are, or null for a service with no public endpoint. The load
    balancer's target group (AWS/ApplicationELB, latency in seconds) or Service Connect (AWS/ECS, latency
    in milliseconds): latency_units_per_ms converts the objective's threshold into the metric's unit.
    target_dimensions, when set, are the errors' and latency metrics' own: Service Connect publishes
    RequestCount under DiscoveryName but HTTPCode_Target_5XX_Count and TargetResponseTime under
    TargetDiscoveryName (CQA-2123, the Amazon ECS CloudWatch metrics page).
  EOT
  type = object({
    namespace            = string
    dimensions           = map(string)
    target_dimensions    = optional(map(string))
    requests_metric      = string
    errors_metric        = string
    latency_metric       = string
    latency_units_per_ms = number
  })
  default = null
}

variable "availability_objective" {
  description = "The share of requests that must not answer 5xx, over 30 days."
  type        = number
  default     = 0.999

  validation {
    condition     = var.availability_objective > 0.9 && var.availability_objective < 1
    error_message = "availability_objective is a share between 0.9 and 1, for example 0.999."
  }
}

variable "latency_objective" {
  description = "The share of requests (target) that must answer within threshold_ms, over 30 days."
  type = object({
    threshold_ms = number
    target       = number
  })
  default = { threshold_ms = 1000, target = 0.99 }
}

variable "queues" {
  description = <<-EOT
    The service's event processing lag, per queue it reads: the age of its oldest message must stay under
    max_age_seconds in a share (target) of the minutes, over 30 days. Pledges' queue carries
    CampaignPublished, whose lag is the publish gap: a pledge for a campaign published a moment ago
    answers 404 campaign_not_found until Pledges has its funding window.
  EOT
  type = map(object({
    queue_name      = string
    max_age_seconds = number
    target          = number
  }))
  default = {}
}

variable "log_group_name" {
  description = "The service's log group, for the dashboard's journey and error widgets."
  type        = string
}
