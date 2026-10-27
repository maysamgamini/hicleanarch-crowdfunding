// Which service answers a path once the four services run apart: the gateway's routes from s5-l03,
// s5-l05 and s5-l08 onwards. Every request of the four-services profile carries the answer as its
// `service` tag, so k6 reports each service's latency and errors on its own, also against one host.

const routes = [
  { method: "PUT", pattern: /^\/v1\/me\/notifications\/[^/]+$/, service: "notifications", endpoint: "PUT /v1/me/notifications/{kind}" },
  { method: "POST", pattern: /^\/v1\/webhooks\/psp$/, service: "payments", endpoint: "POST /v1/webhooks/psp" },
  { method: "PUT", pattern: /^\/v1\/pledges\/[^/]+\/payment-method$/, service: "payments", endpoint: "PUT /v1/pledges/{id}/payment-method" },
  { method: "POST", pattern: /^\/v1\/pledges\/[^/]+\/confirm-payment$/, service: "payments", endpoint: "POST /v1/pledges/{id}/confirm-payment" },
  { method: "GET", pattern: /^\/v1\/campaigns\/[^/]+\/settlement$/, service: "payments", endpoint: "GET /v1/campaigns/{id}/settlement" },
  { method: "POST", pattern: /^\/v1\/campaigns\/[^/]+\/pledges$/, service: "pledges", endpoint: "POST /v1/campaigns/{id}/pledges" },
  { method: "GET", pattern: /^\/v1\/campaigns\/[^/]+\/totals$/, service: "pledges", endpoint: "GET /v1/campaigns/{id}/totals" },
  { method: "POST", pattern: /^\/v1\/campaigns\/[^/]+\/funding\/cancel$/, service: "pledges", endpoint: "POST /v1/campaigns/{id}/funding/cancel" },
  { method: "*", pattern: /^\/v1\/pledges\//, service: "pledges", endpoint: "/v1/pledges/{id}" },
  { method: "POST", pattern: /^\/v1\/campaigns$/, service: "campaigns", endpoint: "POST /v1/campaigns" },
  { method: "POST", pattern: /^\/v1\/campaigns\/[^/]+\/(publish|cancel)$/, service: "campaigns", endpoint: "POST /v1/campaigns/{id}/{action}" },
  { method: "GET", pattern: /^\/v1\/campaigns\/[^/]+$/, service: "campaigns", endpoint: "GET /v1/campaigns/{id}" },
];

export const SERVICES = ["pledges", "campaigns", "payments", "notifications"];

/** The service and endpoint that answer `method path`; throws for a path no service owns. */
export function route(method, path) {
  const found = routes.find((r) => (r.method === "*" || r.method === method) && r.pattern.test(path));
  if (!found) throw new Error(`no service answers ${method} ${path}`);
  return { service: found.service, endpoint: found.endpoint };
}
