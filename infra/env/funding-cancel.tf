# Lesson s5-l05: Pledges serves the creator's LIVE cancel on POST /v1/campaigns/{id}/funding/cancel
# once no LIVE funding window lacks a creator. After the deploy, run the release image with
# `record-creators`, then with `check-creators` until it exits 0, then apply with this set to true.
variable "funding_cancel_open" {
  description = "Whether Pledges serves POST /v1/campaigns/{id}/funding/cancel."
  type        = bool
  default     = false
}
