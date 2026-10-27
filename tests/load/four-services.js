// Four services, measured (lesson s5-l15), run with k6. The same profile runs against one local host
// and against the four services deployed in a learner's own AWS account; every request is tagged with the service that answers it
// once the services run apart (lib/services.js), so the report is per service either way.
//
// Scenarios, at SCALE times the loop's rates:
//   launch_pledges     3,000 pledges a second on one campaign, for a minute (R2.1)
//   launch_pages       1,000 page loads a second: the catalog from Campaigns, the totals from the CDN path
//   preferences        backers turning an email off and on, through Notifications
//   hot_tier           many more backers than places race for a limited tier (the hot row, s3-a03)
//   verify_totals      after the aggregator's few seconds, the displayed totals against what was accepted
//   close_pass         a funded campaign reaches its deadline: decided by Pledges, charged and settled by Payments
//
// Besides the thresholds, the report checks the shape of the design: no tier sells more places than it
// has, the displayed total matches the pledges accepted, and the CDN path (CloudFront in an account, an
// nginx cache with the same 2-second rule locally) sends the origin about one totals request every 2 s.
// The profile runs at any tag from s3-l13 on: every endpoint it calls exists there.
//
// Environment: API_URL; PUBLIC_URL (CloudFront in an AWS account; the API locally); TOKENS_FILE or
// LOCAL_SIGNING_KEY (lib/tokens.js); SCALE; WHERE (local, or account for a run in the learner's own AWS
// account); SETUP (the machine, database and hosts, said in the report); TOPOLOGY; REPORT_DIR.

import http from "k6/http";
import exec from "k6/execution";
import { check, sleep } from "k6";
import { Counter, Gauge, Trend } from "k6/metrics";

import { route, SERVICES } from "./lib/services.js";
import { backerToken, creatorToken } from "./lib/tokens.js";

const API = __ENV.API_URL;
const PUBLIC = __ENV.PUBLIC_URL || API;
const SCALE = Number(__ENV.SCALE || "0.01");
const WHERE = __ENV.WHERE || "local";
const TOPOLOGY = __ENV.TOPOLOGY || "one host composing all four modules";
const SETUP = __ENV.SETUP || "not stated";
// Inside compose.yaml's network the gateway trusts X-Forwarded-For, as it trusts the load balancer's on
// AWS: each backer then comes from an address of its own (198.18.0.0/15, the benchmarking range), as the
// public would, instead of every request sharing k6's one address and its rate limit.
const FORWARD_VIEWERS = __ENV.FORWARD_VIEWERS === "1";
const SLOS = JSON.parse(open("./slos.json")).services;

const PLEDGE_RATE = Math.max(1, Math.round(3000 * SCALE));
const PAGE_RATE = Math.max(1, Math.round(1000 * SCALE));
const LAUNCH_S = 60;
const HOT_RATE = Math.max(5, Math.round(2000 * SCALE));
const HOT_PLACES = Math.max(3, Math.round(1000 * SCALE));
const HOT_START_S = LAUNCH_S + 5;
const HOT_S = 10;
// The aggregator refreshes the displayed totals every few seconds (s3-a03); give it twice that.
const VERIFY_START_S = HOT_START_S + HOT_S + 10;
const CLOSE_PLEDGES = Math.max(10, Math.round(10000 * SCALE));
// The close pass's campaign reaches its deadline after setup has placed its pledges.
const CLOSE_LEAD_S = Math.max(60, 30 + Math.ceil(CLOSE_PLEDGES * 0.05));
// The slowest pace the loop accepts is the PSP's default 25 charges a second, after one close-job period.
const CLOSE_TARGET_S = 60 + Math.ceil(CLOSE_PLEDGES / 25);

const decidedAfter = new Trend("close_decided_after_s");
const settledAfter = new Trend("close_settled_after_s");
const totalsReads = new Counter("totals_reads");
const totalsOrigin = new Counter("totals_origin_requests");
const totalsAge = new Trend("totals_age", true);
const launchAccepted = new Counter("launch_accepted");
const hotPlaced = new Counter("hot_tier_placed");
const launchBackersSeen = new Gauge("launch_backers_seen");
const launchPledgedSeen = new Gauge("launch_pledged_seen");
const hotBackersSeen = new Gauge("hot_backers_seen");
const hotRemainingSeen = new Gauge("hot_remaining_seen");
const totalsSettled = new Gauge("totals_settled_after_s");

const perService = Object.fromEntries(Object.entries(SLOS).flatMap(([name, slo]) => [
  [`http_req_failed{service:${name}}`, [`rate<=${(1 - slo.availability).toFixed(6)}`]],
  [`http_req_duration{endpoint:${slo.latency.endpoint}}`, [`p(99)<${slo.latency.p99Ms}`]],
]));

export const options = {
  setupTimeout: "10m",
  summaryTrendStats: ["avg", "med", "p(95)", "p(99)", "max", "count"],
  scenarios: {
    launch_pledges: {
      executor: "constant-arrival-rate", exec: "pledgeAtLaunch",
      rate: PLEDGE_RATE, timeUnit: "1s", duration: `${LAUNCH_S}s`,
      preAllocatedVUs: Math.max(5, PLEDGE_RATE), maxVUs: Math.max(20, PLEDGE_RATE * 4),
    },
    launch_pages: {
      executor: "constant-arrival-rate", exec: "loadPage",
      rate: PAGE_RATE, timeUnit: "1s", duration: `${LAUNCH_S}s`,
      preAllocatedVUs: Math.max(5, PAGE_RATE), maxVUs: Math.max(20, PAGE_RATE * 4),
    },
    hot_tier: {
      executor: "constant-arrival-rate", exec: "raceForTier", startTime: `${HOT_START_S}s`,
      rate: HOT_RATE, timeUnit: "1s", duration: `${HOT_S}s`,
      preAllocatedVUs: Math.max(5, HOT_RATE), maxVUs: Math.max(20, HOT_RATE * 4),
    },
    verify_totals: {
      executor: "per-vu-iterations", exec: "verifyTotals", startTime: `${VERIFY_START_S}s`, vus: 1, iterations: 1,
    },
    preferences: {
      executor: "constant-arrival-rate", exec: "setPreference",
      rate: 1, timeUnit: "1s", duration: `${LAUNCH_S}s`, preAllocatedVUs: 2, maxVUs: 5,
    },
    close_pass: {
      executor: "per-vu-iterations", exec: "followClose", vus: 1, iterations: 1,
      maxDuration: `${CLOSE_LEAD_S + CLOSE_TARGET_S + 300}s`,
    },
  },
  thresholds: {
    ...perService,
    // Listed so the summary keeps each service's request count.
    ...Object.fromEntries(Object.keys(SLOS).map((name) => [`http_reqs{service:${name}}`, ["count>=0"]])),
    close_settled_after_s: [`max<${CLOSE_TARGET_S}`],
    // A limited tier never sells more than its places, and with far more backers than places it sells them all.
    hot_tier_placed: [`count<=${HOT_PLACES}`, `count>=${HOT_PLACES}`],
    totals_age: ["p(99)<5000"],
  },
};

/** The same caller's address for every request it makes. */
function viewer(token) {
  let h = 0;
  for (let i = 0; i < token.length; i++) h = (h * 31 + token.charCodeAt(i)) >>> 0;
  return `198.${18 + (h & 1)}.${(h >>> 1) & 255}.${(h >>> 9) & 255}`;
}

function key() {
  return `${Date.now().toString(16)}-${Math.random().toString(16).slice(2)}-${Math.random().toString(16).slice(2)}`;
}

/** One request to the API, tagged with the service and endpoint that answer it. */
function call(method, base, path, body, token, expected = [200, 201, 204]) {
  const { service, endpoint } = route(method, path);
  const headers = { "Content-Type": "application/json" };
  if (token) {
    headers.Authorization = `Bearer ${token}`;
    headers["Idempotency-Key"] = key();
    if (FORWARD_VIEWERS) headers["X-Forwarded-For"] = viewer(token);
  }

  return http.request(method, `${base}${path}`, body === null ? null : JSON.stringify(body), {
    headers,
    tags: { service, endpoint, name: endpoint },
    responseCallback: http.expectedStatuses(...expected),
  });
}

function campaign(creator, title, deadline, goalMinor, limit = null) {
  const created = call("POST", API, "/v1/campaigns", {
    title, goal_minor: goalMinor, currency: "USD", deadline, tiers: [{ title: "Copy", min_pledge_minor: 500, limit }],
  }, creator);
  if (created.status !== 201) throw new Error(`create campaign: ${created.status} ${created.body}`);
  const id = created.json("campaign_id");
  const published = call("POST", API, `/v1/campaigns/${id}/publish`, null, creator);
  if (published.status !== 200) throw new Error(`publish: ${published.status} ${published.body}`);
  return id;
}

function pledge(campaignId, token, tierId = null, expected = [201]) {
  return call("POST", API, `/v1/campaigns/${campaignId}/pledges`,
    { amount_minor: 6_000, tier_id: tierId, payment_method_id: "pm_card_visa" }, token, expected);
}

export function setup() {
  const creator = creatorToken();
  const far = new Date(Date.now() + 30 * 86400_000).toISOString();
  const launch = campaign(creator, "Load: launch", far, 100_000);
  const hot = campaign(creator, "Load: hot tier", far, 100_000, HOT_PLACES);
  const hotTier = call("GET", API, `/v1/campaigns/${hot}`, null, null).json("tiers")[0].tier_id;
  const deadline = new Date(Date.now() + CLOSE_LEAD_S * 1000).toISOString();
  // Its goal is half of what setup pledges, so it is funded and every pledge is charged.
  const close = campaign(creator, "Load: close pass", deadline, (CLOSE_PLEDGES * 6_000) / 2);
  sleep(3); // the funding windows open when Pledges handles CampaignPublished

  for (let i = 0; i < CLOSE_PLEDGES; i++) {
    const res = pledge(close, backerToken(i));
    if (res.status !== 201) throw new Error(`close-pass pledge ${i}: ${res.status} ${res.body}`);
  }

  return { creator, launch, hot, hotTier, close, deadline };
}

export function pledgeAtLaunch(data) {
  const res = pledge(data.launch, backerToken(exec.scenario.iterationInTest));
  if (res.status === 201) launchAccepted.add(1);
  check(res, { "pledge accepted": (r) => r.status === 201 });
}

/** Many more backers than places, at once: a sold-out tier answers 409, and never sells one place too many. */
export function raceForTier(data) {
  const res = pledge(data.hot, backerToken(1_000_000 + exec.scenario.iterationInTest), data.hotTier, [201, 409]);
  if (res.status === 201) hotPlaced.add(1);
  check(res, { "placed or sold out": (r) => r.status === 201 || r.json("reason") === "tier_sold_out" });
}

/**
 * The displayed totals, from the origin (not the cache), once the aggregator has caught up: read every
 * second until the hot tier shows every place taken and the launch's numbers stop moving (three equal
 * reads), for at most a minute. How long that took is reported too.
 */
export function verifyTotals(data) {
  const started = Date.now();
  let launch;
  let hot;
  let same = 0;
  while (Date.now() - started < 60_000) {
    const next = call("GET", API, `/v1/campaigns/${data.launch}/totals`, null, null).json();
    hot = call("GET", API, `/v1/campaigns/${data.hot}/totals`, null, null).json();
    same = launch && next.backers === launch.backers && next.pledged_minor === launch.pledged_minor ? same + 1 : 0;
    launch = next;
    if (same >= 2 && hot.tiers[0].remaining === 0) break;
    sleep(1);
  }

  totalsSettled.add((Date.now() - started) / 1000);
  launchBackersSeen.add(launch.backers);
  launchPledgedSeen.add(launch.pledged_minor);
  hotBackersSeen.add(hot.backers);
  hotRemainingSeen.add(hot.tiers[0].remaining);
}

/** A page load: the catalog from Campaigns, and the totals the page polls, through the CDN path. */
export function loadPage(data) {
  call("GET", API, `/v1/campaigns/${data.launch}`, null, null);
  const totals = call("GET", PUBLIC, `/v1/campaigns/${data.launch}/totals`, null, null);
  if (totals.status !== 200) return;
  totalsReads.add(1);
  // Without CloudFront (a local run) every read reaches the origin.
  // CloudFront says "Hit from cloudfront", the local nginx cache "HIT"; anything else reached the origin.
  if (!/^hit/i.test(totals.headers["X-Cache"] || "")) totalsOrigin.add(1);
  totalsAge.add(Math.max(0, Date.now() - Date.parse(totals.json("as_of"))));
}

export function setPreference() {
  const res = call("PUT", API, "/v1/me/notifications/campaign_funded", { enabled: exec.scenario.iterationInTest % 2 === 0 }, backerToken(0));
  check(res, { "preference stored": (r) => r.status === 204 });
}

export function followClose(data) {
  const deadline = Date.parse(data.deadline);
  let decided = false;
  for (;;) {
    const status = call("GET", API, `/v1/campaigns/${data.close}`, null, null).json("status");
    if (!decided && status !== "LIVE") {
      decidedAfter.add((Date.now() - deadline) / 1000);
      decided = true;
    }

    if (status === "SETTLED") {
      settledAfter.add((Date.now() - deadline) / 1000);
      // Payments' settlement progress (from s3-l15; a 404 before it).
      call("GET", API, `/v1/campaigns/${data.close}/settlement`, null, data.creator, [200, 404]);
      return;
    }

    sleep(2);
  }
}

// The report: each service's measured latency and availability next to its objective, the launch,
// the totals' origin requests and the close pass, labelled with where and on what they were measured.
export function handleSummary(summary) {
  const m = summary.metrics;
  const v = (name, stat) => (m[name] && m[name].values[stat] !== undefined ? m[name].values[stat] : null);
  const ok = (name) => !m[name] || !m[name].thresholds || Object.values(m[name].thresholds).every((t) => t.ok);
  const seconds = summary.state.testRunDurationMs / 1000;

  const services = Object.fromEntries(SERVICES.map((name) => {
    const slo = SLOS[name];
    const failed = `http_req_failed{service:${name}}`;
    const latency = `http_req_duration{endpoint:${slo.latency.endpoint}}`;
    const requests = v(`http_reqs{service:${name}}`, "count");
    return [name, {
      requests,
      availability: requests ? 1 - v(failed, "rate") : null,
      objective: { availability: slo.availability, endpoint: slo.latency.endpoint, p99Ms: slo.latency.p99Ms },
      latencyMs: { p50: v(latency, "med"), p95: v(latency, "p(95)"), p99: v(latency, "p(99)") },
      met: requests ? ok(failed) && ok(latency) : null,
    }];
  }));

  const accepted = v("launch_accepted", "count") ?? 0;
  const placed = v("hot_tier_placed", "count") ?? 0;
  const origin = v("totals_origin_requests", "count") ?? 0;
  const cdn = PUBLIC !== API;
  const shape = [
    { what: "Hot tier: places sold, with far more backers than places", expected: HOT_PLACES, observed: placed, ok: placed === HOT_PLACES },
    { what: "Hot tier: backers in the displayed total", expected: HOT_PLACES, observed: v("hot_backers_seen", "value"), ok: v("hot_backers_seen", "value") === HOT_PLACES },
    { what: "Hot tier: places left in the displayed total", expected: 0, observed: v("hot_remaining_seen", "value"), ok: v("hot_remaining_seen", "value") === 0 },
    { what: "Launch: backers in the displayed total = pledges accepted", expected: accepted, observed: v("launch_backers_seen", "value"), ok: v("launch_backers_seen", "value") === accepted },
    { what: "Launch: amount in the displayed total = accepted × 6,000", expected: accepted * 6_000, observed: v("launch_pledged_seen", "value"), ok: v("launch_pledged_seen", "value") === accepted * 6_000 },
    cdn
      ? { what: "CDN path: origin requests for one campaign's totals, about one every 2 s", expected: `≤ ${Math.ceil(LAUNCH_S / 2) + 2}`, observed: origin, ok: origin <= Math.ceil(LAUNCH_S / 2) + 2 }
      : { what: "CDN path: not in front (every read reaches the origin)", expected: "—", observed: origin, ok: true },
  ];

  const report = {
    profile: "four-services",
    where: WHERE,
    topology: TOPOLOGY,
    setup: SETUP,
    numbers: WHERE === "local"
      ? "local measurements: one machine, one database, an nginx cache standing in for CloudFront; they show whether the design's shape holds, not what AWS would do"
      : "measured in the learner's own AWS account",
    scale: SCALE,
    measuredAt: new Date().toISOString(),
    services,
    launch: {
      seconds: LAUNCH_S,
      pledgesPerSecond: PLEDGE_RATE,
      pageLoadsPerSecond: PAGE_RATE,
      pledgesAccepted: accepted,
    },
    totals: {
      reads: v("totals_reads", "count") ?? 0,
      originRequests: v("totals_origin_requests", "count") ?? 0,
      originPerSecond: (v("totals_origin_requests", "count") ?? 0) / Math.max(1, LAUNCH_S),
      ageP99Ms: v("totals_age", "p(99)"),
      cdn,
    },
    displayedTotalsSettledAfterS: v("totals_settled_after_s", "value"),
    closePass: {
      pledges: CLOSE_PLEDGES,
      decidedAfterS: v("close_decided_after_s", "max"),
      settledAfterS: v("close_settled_after_s", "max"),
      targetS: CLOSE_TARGET_S,
    },
    shape,
    shapeHolds: shape.every((c) => c.ok),
    runSeconds: seconds,
    thresholdsPassed: Object.keys(m).every(ok),
  };

  const fmt = (x, unit = "") => (x === null || x === undefined ? "not measured" : `${Math.round(x * 10) / 10}${unit}`);
  const md = [
    `## Four services at ${SCALE} × the loop's rates (${WHERE})`,
    "",
    `Served by: ${TOPOLOGY}. Setup: ${SETUP}.`,
    "",
    `These are ${report.numbers}.`,
    "",
    "| Service | Requests | Availability (objective) | P99 of its key endpoint (objective) | Met |",
    "|---|---|---|---|---|",
    ...SERVICES.map((name) => {
      const s = services[name];
      return `| ${name} | ${s.requests ?? 0} | ${s.availability === null ? "no requests" : `${(s.availability * 100).toFixed(3)}%`} (${(s.objective.availability * 100).toFixed(2)}%) | ${fmt(s.latencyMs.p99, " ms")} (< ${s.objective.p99Ms} ms, ${s.objective.endpoint}) | ${s.met === null ? "—" : s.met ? "yes" : "NO"} |`;
    }),
    "",
    `Totals: ${report.totals.reads} reads, ${report.totals.originRequests} reached the origin (${fmt(report.totals.originPerSecond, " a second")}${report.totals.cdn ? "" : ", no CDN in front"}); displayed total P99 age ${fmt(report.totals.ageP99Ms, " ms")}.`,
    "",
    "| Shape check | Expected | Observed | Holds |",
    "|---|---|---|---|",
    ...shape.map((c) => `| ${c.what} | ${c.expected} | ${c.observed ?? "not observed"} | ${c.ok ? "yes" : "NO"} |`),
    "",
    `Close pass: ${CLOSE_PLEDGES} pledges; decided ${fmt(report.closePass.decidedAfterS, " s")} and settled ${fmt(report.closePass.settledAfterS, " s")} after the deadline (target < ${CLOSE_TARGET_S} s).`,
    "",
    `Thresholds ${report.thresholdsPassed ? "passed" : "FAILED"}.`,
    "",
  ].join("\n");

  const dir = __ENV.REPORT_DIR || "artifacts/load";
  return {
    [`${dir}/four-services.k6.json`]: JSON.stringify(report, null, 2),
    [`${dir}/four-services.k6.md`]: md,
    stdout: md,
  };
}
