// A launch, as a baseline: many readers of one campaign page, and backers pledging to it.
//
//   k6 run -e BASE_URL=http://<alb> -e CAMPAIGN_ID=cmp_... -e BACKER_TOKENS=tokens.txt tests/load/launch.js
//
// BACKER_TOKENS is a file with one backer token per line (each backer may pledge once per
// campaign). The numbers this produces are recorded in the lesson only as measured.
import http from 'k6/http';
import { check } from 'k6';
import { SharedArray } from 'k6/data';
import exec from 'k6/execution';

const base = __ENV.BASE_URL;
const campaign = __ENV.CAMPAIGN_ID;
const tokens = new SharedArray('tokens', () => open(__ENV.BACKER_TOKENS).split('\n').filter((t) => t.trim() !== ''));

export const options = {
  scenarios: {
    readers: {
      executor: 'ramping-arrival-rate',
      exec: 'read',
      startRate: 10,
      timeUnit: '1s',
      preAllocatedVUs: 50,
      maxVUs: 400,
      stages: [
        { target: 200, duration: '1m' },
        { target: 200, duration: '3m' },
        { target: 0, duration: '1m' },
      ],
    },
    backers: {
      executor: 'shared-iterations',
      exec: 'pledge',
      vus: 50,
      iterations: tokens.length,
      maxDuration: '5m',
    },
  },
  thresholds: {
    'http_req_duration{scenario:readers}': ['p(99)<500'],
    'http_req_duration{scenario:backers}': ['p(99)<1000'],
    'checks': ['rate>0.99'],
  },
};

export function read() {
  const res = http.get(`${base}/v1/campaigns/${campaign}`);
  check(res, { 'campaign read': (r) => r.status === 200 });
}

export function pledge() {
  const token = tokens[exec.scenario.iterationInTest];
  const res = http.post(
    `${base}/v1/campaigns/${campaign}/pledges`,
    JSON.stringify({ amount_minor: 6000, tier_id: null, payment_method_id: 'pm_load_test' }),
    {
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${token}`,
        'Idempotency-Key': `${exec.scenario.iterationInTest}-${campaign}`,
      },
    },
  );
  check(res, { 'pledge accepted': (r) => r.status === 201 });
}
