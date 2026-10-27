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
  description = "The Payments.Host image of this release, as repository URL and tag. The migration task runs it."
  type        = string
  default     = "public.ecr.aws/docker/library/nginx:stable" # only so that destroy needs no argument
}

variable "service_image" {
  description = "What the service runs. The deploy keeps the current one until the release's migration has run, then sets it to image. Empty: no service yet."
  type        = string
  default     = ""
}

variable "desired_count" {
  description = "Payments tasks. Its jobs claim work with FOR UPDATE SKIP LOCKED, so more tasks never do a piece twice."
  type        = number
  default     = 1
}
