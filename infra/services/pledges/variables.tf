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
  description = "The Pledges.Host image of this release, as repository URL and tag. The migrate task runs it."
  type        = string
  default     = "public.ecr.aws/docker/library/nginx:stable" # only so that destroy needs no argument
}

variable "service_image" {
  description = "What the service runs. The deploy keeps the current one until the release's migration has run, then sets it to image. Empty: no service yet."
  type        = string
  default     = ""
}

# Lesson s5-l05: Pledges serves the creator's LIVE cancel on POST /v1/campaigns/{id}/funding/cancel once no
# LIVE funding window lacks a creator: run the release image with `check-creators` until it exits 0, then
# apply with this set to true.
variable "funding_cancel_open" {
  description = "Whether Pledges serves POST /v1/campaigns/{id}/funding/cancel."
  type        = bool
  default     = false
}

variable "launch" {
  description = "A known launch: from start to end (UTC, RFC 3339 such as 2026-11-02T15:45:00Z) at least min_count tasks run, started before the crowd arrives. Null: none scheduled."
  type = object({
    start     = string
    end       = string
    min_count = number
  })
  default = null

  validation {
    condition = var.launch == null || (
      can(timecmp(var.launch.start, var.launch.end)) && endswith(var.launch.start, "Z") && endswith(var.launch.end, "Z")
      && try(timecmp(var.launch.start, var.launch.end) < 0, false)
    )
    error_message = "launch.start and launch.end must be UTC times (ending in Z), start before end."
  }
}
