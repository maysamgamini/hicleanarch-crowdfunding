// The Round 2 load profile (lesson s3-l13), run with k6 against a deployed environment.
//
// Four scenarios, at SCALE times the loop's rates (SCALE=1 is the full Round 2 launch):
//   launch_burst         3,000 pledges a second for a minute, all on one campaign
//   hot_tier             20,000 backers in 10 seconds for 1,000 Early Bird places
//   totals_reads         campaign totals read through CloudFront the whole time
//   deadline_settlement  a funded campaign reaches its deadline and is settled
//
// The thresholds are the loop's targets; a run that misses one fails. Measured numbers go into
// artifacts/load/report.md and report.json (see handleSummary below).
//
// Environment: API_URL, PUBLIC_URL (CloudFront), TOKENS_FILE (from scripts/load.sh), SCALE.

import http from "k6/http";
import exec from "k6/execution";
import { check, sleep } from "k6";
import { SharedArray } from "k6/data";
import { Counter, Trend } from "k6/metrics";

const API = __ENV.API_URL;
const PUBLIC = __ENV.PUBLIC_URL || __ENV.API_URL;
const SCALE = Number(__ENV.SCALE || "0.02");

// The loop's numbers (R2.1, R2.2, R2.6), scaled.
const LAUNCH_RATE = Math.max(1, Math.round(3000 * SCALE)); // pledges a second on one campaign
const HOT_RATE = Math.max(1, Math.round(2000 * SCALE)); // 20,000 in 10 s
const HOT_PLACES = Math.max(1, Math.round(1000 * SCALE));
const READ_RATE = Math.max(1, Math.round(1000 * SCALE));
const SETTLE_PLEDGES = Math.max(10, Math.round(10000 * SCALE));
// The settlement campaign's deadline, after setup: time to place its pledges one by one first.
const SETTLE_LEAD_S = Math.max(240, 120 + Math.ceil(SETTLE_PLEDGES * 0.05));
// The loop's slowest acceptable pace is the PSP's default 25 charges a second (11.1 h for a
// million); the close job runs every 30 s. A settlement slower than that misses the target.
const SETTLE_TARGET_S = SETTLE_LEAD_S + 60 + Math.ceil(SETTLE_PLEDGES / 25);

const backers = new SharedArray("backers", () => JSON.parse(open(__ENV.TOKENS_FILE)).backers);
const creator = JSON.parse(open(__ENV.TOKENS_FILE)).creator;

const pledgeLatency = new Trend("pledge_latency", true);
const totalsStaleness = new Trend("totals_staleness", true);
const totalsRead = new Counter("totals_read");
const settlementSeconds = new Trend("settlement_seconds");
const hotTierPlaced = new Counter("hot_tier_placed");
const serverErrors = new Counter("server_errors");

export const options = {
  setupTimeout: "10m",
  // The targets are P99s; k6 does not record the 99th percentile unless asked.
  summaryTrendStats: ["avg", "min", "med", "max", "p(95)", "p(99)"],
  scenarios: {
    launch_burst: {
      executor: "constant-arrival-rate",
      exec: "launchBurst",
      rate: LAUNCH_RATE,
      timeUnit: "1s",
      duration: "60s",
      preAllocatedVUs: Math.max(10, LAUNCH_RATE),
      maxVUs: Math.max(50, LAUNCH_RATE * 4),
    },
    hot_tier: {
      executor: "constant-arrival-rate",
      exec: "hotTier",
      startTime: "70s",
      rate: HOT_RATE,
      timeUnit: "1s",
      duration: "10s",
      preAllocatedVUs: Math.max(10, HOT_RATE),
      maxVUs: Math.max(50, HOT_RATE * 4),
    },
    totals_reads: {
      executor: "constant-arrival-rate",
      exec: "readTotals",
      rate: READ_RATE,
      timeUnit: "1s",
      duration: "90s",
      preAllocatedVUs: Math.max(5, Math.round(READ_RATE / 10)),
      maxVUs: Math.max(20, READ_RATE),
    },
    deadline_settlement: {
      executor: "per-vu-iterations",
      exec: "waitForSettlement",
      vus: 1,
      iterations: 1,
      maxDuration: `${SETTLE_TARGET_S + 600}s`,
    },
  },
  thresholds: {
    // Pledge P99 < 200 ms at the load balancer (R2.1).
    "pledge_latency{scenario:launch_burst}": ["p(99)<200"],
    "pledge_latency{scenario:hot_tier}": ["p(99)<200"],
    // 99.99% (R2.1): at most one request in 10,000 fails on our side.
    server_errors: [`count<=${Math.floor((LAUNCH_RATE * 60 + HOT_RATE * 10 + READ_RATE * 90) / 10000)}`],
    // The displayed total is at most about 5 seconds old (R2.1).
    "totals_staleness": ["p(99)<5000"],
    // A limited tier never sells more than its limit (s1-a02): never more 201s than places.
    hot_tier_placed: [`count<=${HOT_PLACES}`],
    // The funded campaign is settled within the pace the loop accepts.
    settlement_seconds: [`max<${SETTLE_TARGET_S}`],
  },
};

// One idempotency key per attempt (lesson s1-a04). Setup runs outside any scenario, so the key
// does not use the scenario's iteration number.
function key() {
  return `${Date.now().toString(16)}-${Math.random().toString(16).slice(2)}-${Math.random().toString(16).slice(2)}`;
}

function auth(token) {
  return { Authorization: `Bearer ${token}`, "Content-Type": "application/json", "Idempotency-Key": key() };
}

function recordErrors(res) {
  if (res.status >= 500 || res.status === 0) serverErrors.add(1);
}

function createCampaign(title, deadline, tiers, goalMinor = 100_000) {
  const created = http.post(`${API}/v1/campaigns`, JSON.stringify({
    title, goal_minor: goalMinor, currency: "USD", deadline, tiers,
  }), { headers: auth(creator) });
  if (created.status !== 201) throw new Error(`create campaign: ${created.status} ${created.body}`);
  const id = created.json("campaign_id");
  const published = http.post(`${API}/v1/campaigns/${id}/publish`, null, { headers: auth(creator) });
  if (published.status !== 200) throw new Error(`publish: ${published.status} ${published.body}`);
  const campaign = http.get(`${API}/v1/campaigns/${id}`).json();
  return { id, tiers: campaign.tiers.map((t) => t.tier_id) };
}

function pledge(campaignId, token, tierId, amount) {
  return http.post(`${API}/v1/campaigns/${campaignId}/pledges`, JSON.stringify({
    amount_minor: amount, tier_id: tierId, payment_method_id: "pm_card_visa",
  }), { headers: auth(token), tags: { kind: "pledge" }, responseCallback: http.expectedStatuses(201, 409) });
}

export function setup() {
  const far = new Date(Date.now() + 30 * 86400_000).toISOString();
  const launch = createCampaign("Load: launch", far, [{ title: "Standard", min_pledge_minor: 500, limit: null }]);
  const hot = createCampaign("Load: hot tier", far, [{ title: "Early Bird", min_pledge_minor: 500, limit: HOT_PLACES }]);

  const deadline = new Date(Date.now() + SETTLE_LEAD_S * 1000).toISOString();
  // Its goal is half of what setup pledges, so it is funded and every pledge is charged.
  const settle = createCampaign("Load: settlement", deadline, [{ title: "Copy", min_pledge_minor: 500, limit: null }], (SETTLE_PLEDGES * 6_000) / 2);
  sleep(3); // the funding windows open when Pledges handles CampaignPublished, just after the commit

  // Enough pledges, before the deadline, to fund the settlement campaign.
  for (let i = 0; i < SETTLE_PLEDGES; i++) {
    const res = pledge(settle.id, backers[i % backers.length], null, 6_000);
    if (res.status !== 201) throw new Error(`settlement pledge ${i}: ${res.status} ${res.body}`);
  }
  return { launch, hot, settle, settleDeadline: deadline };
}

export function launchBurst(data) {
  const token = backers[exec.scenario.iterationInTest % backers.length];
  const res = pledge(data.launch.id, token, null, 6_000);
  pledgeLatency.add(res.timings.duration);
  recordErrors(res);
  check(res, { "pledge accepted": (r) => r.status === 201 });
}

export function hotTier(data) {
  const token = backers[exec.scenario.iterationInTest % backers.length];
  const res = pledge(data.hot.id, token, data.hot.tiers[0], 6_000);
  pledgeLatency.add(res.timings.duration);
  recordErrors(res);
  if (res.status === 201) hotTierPlaced.add(1);
  check(res, { "placed or sold out": (r) => r.status === 201 || r.json("reason") === "tier_sold_out" });
}

export function readTotals(data) {
  const res = http.get(`${PUBLIC}/v1/campaigns/${data.launch.id}/totals`, { tags: { kind: "totals" } });
  recordErrors(res);
  if (res.status === 200) {
    totalsRead.add(1);
    const asOf = Date.parse(res.json("as_of"));
    totalsStaleness.add(Math.max(0, Date.now() - asOf));
  }
}

export function waitForSettlement(data) {
  const deadline = Date.parse(data.settleDeadline);
  for (;;) {
    const status = http.get(`${API}/v1/campaigns/${data.settle.id}`).json("status");
    if (status === "SETTLED") {
      settlementSeconds.add((Date.now() - deadline) / 1000 + SETTLE_LEAD_S);
      return;
    }
    sleep(10);
  }
}

// The report: the loop's target next to what this run measured.
export function handleSummary(summary) {
  const m = summary.metrics;
  const value = (name, stat) => (m[name] && m[name].values[stat] !== undefined ? m[name].values[stat] : null);
  const rows = [
    ["Pledge P99 at the launch burst", "< 200 ms", value("pledge_latency{scenario:launch_burst}", "p(99)"), "ms"],
    ["Pledge P99 on the hot tier", "< 200 ms", value("pledge_latency{scenario:hot_tier}", "p(99)"), "ms"],
    ["Server errors", "≤ 1 in 10,000", value("server_errors", "count") ?? 0, ""],
    ["Displayed total, P99 age", "< 5 s", value("totals_read", "count") ? value("totals_staleness", "p(99)") : null, "ms"],
    ["Early Bird places sold", `≤ ${HOT_PLACES}`, value("hot_tier_placed", "count") ?? 0, ""],
    ["Deadline to SETTLED", `< ${SETTLE_TARGET_S} s`, value("settlement_seconds", "max"), "s"],
  ];
  const report = {
    scale: SCALE,
    rates: { launch_per_s: LAUNCH_RATE, hot_per_s: HOT_RATE, hot_places: HOT_PLACES, reads_per_s: READ_RATE, settlement_pledges: SETTLE_PLEDGES },
    results: rows.map(([what, target, measured, unit]) => ({ what, target, measured, unit })),
    thresholds_passed: Object.values(m).every((metric) => !metric.thresholds || Object.values(metric.thresholds).every((t) => t.ok)),
  };
  const md = [
    `# Round 2 load profile at ${SCALE} × the loop's rates`,
    "",
    "| What | Target | Measured |",
    "|---|---|---|",
    ...report.results.map((r) => `| ${r.what} | ${r.target} | ${r.measured === null ? "not measured" : `${Math.round(r.measured * 10) / 10} ${r.unit}`.trim()} |`),
    "",
    `Thresholds ${report.thresholds_passed ? "passed" : "FAILED"}.`,
    "",
  ].join("\n");
  const dir = __ENV.REPORT_DIR || "artifacts/load";
  return {
    [`${dir}/report.json`]: JSON.stringify(report, null, 2),
    [`${dir}/report.md`]: md,
    stdout: md,
  };
}
