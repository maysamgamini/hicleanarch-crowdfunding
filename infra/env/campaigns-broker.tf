# Lesson s5-l07: Campaigns' events cross the broker while Campaigns still runs in the monolith.
variable "campaigns_on_broker" {
  description = "Whether the events between Campaigns and the other modules go through SNS and SQS (true) or stay in process (false, the rollback)."
  type        = bool
  default     = true
}

locals {
  # The routes of s5-l07: the events between Campaigns and the other modules go through the broker, and
  # the monolith reads the campaigns queue, while Campaigns still runs in it. Campaigns' own events
  # (the catalog's cache invalidation) stay in process. Rollback: campaigns_on_broker = false, and the
  # monolith still reads the campaigns queue, so messages already queued are handled; the inbox drops a repeat.
  campaigns_broker_routes = var.campaigns_on_broker ? [
    { name = "Events__Routes__Campaigns", value = "Broker" },
    { name = "Events__Producers__Campaigns__Campaigns", value = "InProcess" },
    { name = "Events__Producers__Campaigns__Pledges", value = "Broker" },
    { name = "Events__Producers__Campaigns__Notifications", value = "Broker" },
  ] : []
}
