variable "region" {
  description = "AWS Region; the same as the bootstrap layer."
  type        = string
  default     = "us-east-1"
}

variable "project" {
  description = "Prefix for resource names."
  type        = string
  default     = "hicleanarch"
}

variable "image" {
  description = "The release: the API image the migration task runs, as repository URL and tag, for example <account>.dkr.ecr.us-east-1.amazonaws.com/hicleanarch/crowdfunding-api:<git sha>."
  type        = string
  default     = "public.ecr.aws/docker/library/nginx:stable" # only so that destroy needs no argument
}

variable "service_image" {
  description = "The API image the service runs: the current one until the release is migrated, then the release. Empty: no service yet (a new environment, before its first migration)."
  type        = string
  default     = ""
}

variable "service_task_definition" {
  description = "The task definition the API service keeps while the release is migrated (deploy-monolith.sh prepare passes the one it runs). Empty: this layer's own api task definition."
  type        = string
  default     = ""
}

variable "zone_count" {
  description = "Availability Zones the subnets, load balancers and tasks span. Two keep the sandbox cheap; production runs three, so losing one leaves two thirds of the capacity, not half."
  type        = number
  default     = 2

  validation {
    condition     = var.zone_count >= 2 && var.zone_count <= 3
    error_message = "zone_count must be 2 or 3: Aurora, ElastiCache Multi-AZ and the load balancers need at least two."
  }
}

variable "desired_count" {
  description = "How many copies of the program run; each runs every module and every job. ECS spreads them over the zones, so a multiple of zone_count keeps the zones even."
  type        = number
  default     = 2

  validation {
    condition     = var.desired_count >= var.zone_count
    error_message = "desired_count must be at least zone_count: a task in every zone, so losing one zone leaves the others serving while ECS replaces the lost tasks."
  }
}

variable "max_count" {
  description = "The most copies autoscaling may run, for a launch."
  type        = number
  default     = 6
}

variable "reader_count" {
  description = "Aurora readers behind the reader endpoint; 0 sends reads to the writer."
  type        = number
  default     = 1
}

variable "aurora_engine_version" {
  description = "Aurora PostgreSQL version; 17 is the engine the course targets."
  type        = string
  default     = "17.4"
}

variable "collector_image" {
  description = "AWS Distro for OpenTelemetry collector, run beside the program in each task."
  type        = string
  default     = "public.ecr.aws/aws-observability/aws-otel-collector:v0.49.0"
}

variable "notifications_from" {
  description = "The address emails are sent from. Empty: emails stay in memory (nothing is sent)."
  type        = string
  default     = ""
}

variable "alarm_email" {
  description = "Where alarms are sent. Empty: alarms change state, and nobody is emailed."
  type        = string
  default     = ""
}
