variable "project" {
  description = "Prefix for resource names; the shared layer's."
  type        = string
}

variable "region" {
  description = "AWS Region; the shared layer's."
  type        = string
}

variable "name" {
  description = "The service's name: its ECS service, its Service Connect name, its log group and the middle of every resource name (<project>-sandbox-<name>-*)."
  type        = string
}

variable "shared" {
  description = "The parts of the shared layer's published outputs (infra/env/shared-outputs.tf) every service needs."
  type = object({
    cluster_name                  = string
    cluster_arn                   = string
    vpc_cidr                      = string
    task_subnet_ids               = list(string)
    service_connect_namespace_arn = string
    collector_image               = string
    cognito_issuer                = string
    user_pool_client_id           = string
    alarm_topic_arn               = string
  })
}

variable "security_group_id" {
  description = "The service's own security group, which the shared layer owns with the rest of the network and which admits named callers only."
  type        = string
}

variable "image" {
  description = "This release's image, as repository URL and tag. The migrate task runs it."
  type        = string
}

variable "service_image" {
  description = "What the service runs. The deploy keeps the current one until the release's migration has run, then sets it to image. Empty: no service yet."
  type        = string
}

variable "desired_count" {
  description = "The service's tasks."
  type        = number
}

# Every service joins Service Connect, and ECS adds its proxy to each task. AWS recommends 256 CPU
# units and at least 64 MiB for the proxy (CQA-2106): the default is 256 units for the program plus
# the proxy's, at Fargate's matching memory.
variable "cpu" {
  type    = number
  default = 512
}

variable "memory" {
  type    = number
  default = 1024
}

variable "secrets" {
  description = "The container's secrets, as ECS takes them ({ name, valueFrom }). The execution role may read these and no other."
  type        = list(object({ name = string, valueFrom = string }))
}

variable "migrate_secrets" {
  description = "What the migrate task adds to the secrets: the service's migration role (ConnectionStrings__Migrator), or for a cluster of its own its owner. Only the migrate task's execution role may read them."
  type        = list(object({ name = string, valueFrom = string }))
}

variable "environment" {
  description = "The container's own settings, beside the platform's (telemetry, sign-in, forwarded headers)."
  type        = list(object({ name = string, value = string }))
  default     = []
}

variable "queue_arn" {
  description = "The service's own queue, which its task role may read. Null: it reads none."
  type        = string
  default     = null
}

variable "topic_arn" {
  description = "The service's own topic, to which its task role may publish and whose subscriptions it may check. Null: it publishes nothing."
  type        = string
  default     = null
}

variable "task_policy_json" {
  description = "What else the running service may do: an AWS API of its own (SES, Cognito). Empty: nothing more."
  type        = string
  default     = ""
}

variable "internal_port" {
  description = "A second port, 8081, for internal commands from other services (as Payments'), with this per-request timeout on the callers' proxies. Null: the public port only."
  type        = object({ per_request_timeout_seconds = number })
  default     = null
}

variable "assembly" {
  description = "The host's assembly, as the image runs it (CrowdFunding.<Service>.Host.dll): the container's health check runs it with `health` (HealthProbe), which asks the program's own /health/ready."
  type        = string
}

variable "objectives" {
  description = <<-EOT
    What the service promises over 30 days (lesson s5-l13): the share of its public requests that must
    not answer 5xx, the share that must answer within threshold_ms, and, if it reads a queue, the share
    of minutes in which no message on it is older than max_age_seconds. Each objective pages fast and
    slow (infra/modules/service-signals).
  EOT
  type = object({
    availability = optional(number, 0.999)
    latency      = optional(object({ threshold_ms = number, target = number }), { threshold_ms = 1000, target = 0.99 })
    lag          = optional(object({ max_age_seconds = number, target = number }), { max_age_seconds = 60, target = 0.99 })
  })
  default = {}
}

variable "service_task_definition" {
  description = "The task definition the service keeps while the release is migrated (CQA-2103): deploy-service.sh's first apply passes the one it runs. Empty: this release's."
  type        = string
  default     = ""
}

variable "kept_secret_arns" {
  description = "The previous release's secrets (its release_secret_arns output), which the execution role keeps for one more release (CQA-2124): the revision the first apply keeps, and a rollback, still name them. deploy-service.sh and deploy-payments.sh pass them to both applies. Empty: this release's only."
  type        = list(string)
  default     = []
}
