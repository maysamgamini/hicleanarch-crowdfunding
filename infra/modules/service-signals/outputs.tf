output "page_alarm_names" {
  description = "The composite alarms that page, one per objective and speed."
  value       = [for a in aws_cloudwatch_composite_alarm.burn : a.alarm_name]
}

output "dashboard_name" {
  value = aws_cloudwatch_dashboard.service.dashboard_name
}

output "burn_windows" {
  description = "Every window alarm's parameters, as the alarms use them; the tests evaluate synthetic traffic against them."
  value = merge(
    { for k, w in local.request_windows : k => {
      kind      = "requests"
      objective = w.objective
      speed     = w.speed
      window    = w.window
      seconds   = w.seconds
      factor    = w.factor
      target    = w.target
    } },
    { for k, w in local.lag_windows : k => {
      kind            = "minutes"
      objective       = "lag-${w.queue}"
      speed           = w.speed
      window          = w.window
      minutes         = w.minutes
      bad_minutes     = w.bad_minutes
      max_age_seconds = w.max_age_seconds
    } },
  )
}
