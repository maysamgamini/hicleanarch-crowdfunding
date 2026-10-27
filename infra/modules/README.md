OpenTofu modules.

- `service` (lesson s5-l01): one service on the shared cluster. Its execution role reads its own
  secrets only, its migrate task's execution role adds its migration role's secret, its task role holds
  telemetry plus its own queue and topic, and its ECS service runs in the security group the shared
  layer gives it, reached by name through Service Connect. Every service root calls it.

- `service-signals` (lesson s5-l13): one service's objectives, error budgets, multi-window burn-rate
  alarms (CloudWatch metric math alarms, paged through composite alarms) and its dashboard. The
  `service` module calls it for every service; infra/env calls it for the gateway. Its tests evaluate
  synthetic burns against the alarms' own windows (`tests/evaluate`).
