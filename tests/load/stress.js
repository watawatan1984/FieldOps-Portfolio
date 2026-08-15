import { check } from 'k6';
import http from 'k6/http';
import { verifyAllRoleLogins } from './lib/auth.js';
import { runFieldOpsTraffic } from './lib/scenarios.js';

const baseUrl = __ENV.TARGET_URL || 'http://host.docker.internal:5085';
const stressThreshold = __ENV.FORCE_THRESHOLD_FAILURE === 'true' ? 'p(95)<0' : 'p(95)<=2000';

export const options = {
  vus: 100,
  duration: '5m',
  summaryTrendStats: ['min', 'avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  thresholds: {
    http_req_failed: ['rate==0'],
    checks: ['rate==1'],
    'http_req_duration{profile:stress}': [stressThreshold],
  },
};

export function setup() {
  const response = http.post(`${baseUrl}/__load-test/preflight?vus=100`, null, { tags: { area: 'preflight' } });
  check(response, {
    'preflight succeeds': (item) => item.status === 200,
    'preflight ready': (item) => item.json('ready') === true,
  });
  return {
    cookieHeader: verifyAllRoleLogins(baseUrl),
  };
}

export default function (data) {
  runFieldOpsTraffic(baseUrl, 'stress', data.cookieHeader);
}

export function teardown() {
  const response = http.get(`${baseUrl}/__load-test/postflight`, { tags: { area: 'postflight' } });
  check(response, {
    'postflight succeeds': (item) => item.status === 200,
    'postflight integrity passes': (item) => item.json('integrity.passed') === true,
    'postflight has no active reset': (item) => item.json('activeResetCount') === 0,
  });
}
