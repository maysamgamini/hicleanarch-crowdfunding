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
  description = "The API image to run, as repository URL and tag, for example <account>.dkr.ecr.us-east-1.amazonaws.com/hicleanarch/crowdfunding-api:<git sha>."
  type        = string
  default     = "public.ecr.aws/docker/library/nginx:stable" # only so that destroy needs no argument
}

variable "desired_count" {
  description = "How many copies of the program run; each runs every module and every job."
  type        = number
  default     = 2
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
