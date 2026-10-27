# Synthetic traffic against the module's windows, for the tests: which window alarms would be in ALARM,
# and which pages (both windows of a speed) would fire. Request windows read errors and requests per
# window; lag windows read the queue's oldest-message age for each of the last minutes, newest last.
variable "windows" {
  type = any
}

variable "synthetic_requests" {
  description = "Per objective, and per window length in seconds: bad and total requests in the last that many seconds."
  type        = map(map(object({ bad = number, total = number })))
  default     = {}
}

variable "synthetic_ages" {
  description = "Per lag objective: the oldest message's age at each minute, oldest first."
  type        = map(list(number))
  default     = {}
}

locals {
  firing = {
    for k, w in var.windows : k => (
      w.kind == "requests"
      ? try(var.synthetic_requests[w.objective][tostring(w.seconds)].total > 0 && (var.synthetic_requests[w.objective][tostring(w.seconds)].bad / var.synthetic_requests[w.objective][tostring(w.seconds)].total) / (1 - w.target) >= w.factor, false)
      : try(length([for a in slice(var.synthetic_ages[w.objective], max(0, length(var.synthetic_ages[w.objective]) - w.minutes), length(var.synthetic_ages[w.objective])) : a if a > w.max_age_seconds]) >= w.bad_minutes, false)
    )
  }
  pages = distinct([for k, w in var.windows : "${w.objective}-${w.speed}" if local.firing[k] && local.firing[replace(k, "-${w.window}", w.window == "long" ? "-short" : "-long")]])
}

output "firing" {
  value = local.firing
}

output "pages" {
  value = local.pages
}
