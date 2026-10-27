OpenTofu modules.

- `service` (lesson s5-l01): one service on the shared cluster. Its execution role reads its own
  secrets only, its migrate task's execution role adds its migration role's secret, its task role holds
  telemetry plus its own queue and topic, and its ECS service runs in the security group the shared
  layer gives it, reached by name through Service Connect. Every service root calls it.
