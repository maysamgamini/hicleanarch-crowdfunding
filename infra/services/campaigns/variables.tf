variable "region" {
  description = "AWS Region; the shared layer's."
  type        = string
  default     = "us-east-1"
}

variable "project" {
  description = "Prefix for resource names; the shared layer's."
  type        = string
  default     = "hicleanarch"
}

variable "image" {
  description = "The Campaigns.Host image of this release, as repository URL and tag. The migrate task runs it."
  type        = string
  default     = "public.ecr.aws/docker/library/nginx:stable" # only so that destroy needs no argument
}

variable "service_image" {
  description = "What the service runs. The deploy keeps the current one until the release's migration has run, then sets it to image. Empty: no service yet."
  type        = string
  default     = ""
}

variable "desired_count" {
  description = "Campaigns tasks, one per zone."
  type        = number
  default     = 2
}

variable "service_task_definition" {
  description = "The task definition the service keeps while the release is migrated (CQA-2103): the deploy script's first apply passes the one it runs. Empty: this release's."
  type        = string
  default     = ""
}
