import http from 'k6/http';
import { check, sleep } from 'k6';

export function runFieldOpsTraffic(baseUrl, profile, cookieHeader) {
  const bucket = (__ITER % 10) + 1;
  if (bucket <= 7) {
    readTraffic(baseUrl, profile, cookieHeader);
  } else if (bucket <= 9) {
    isolatedWriteTraffic(baseUrl, profile);
  } else {
    dashboardTraffic(baseUrl, profile, cookieHeader);
  }

  sleep(1);
}

function readTraffic(baseUrl, profile, cookieHeader) {
  const response = http.get(`${baseUrl}/parties?branchId=00000000-0000-4000-8000-000000000001&page=1&pageSize=10`, {
    tags: { profile, traffic: 'read' },
    headers: { Cookie: cookieHeader },
  });
  check(response, {
    'read request succeeds': (item) => item.status === 200,
  });
}

function isolatedWriteTraffic(baseUrl, profile) {
  const response = http.post(`${baseUrl}/__load-test/write/${__VU}`, null, {
    tags: { profile, traffic: 'isolated_write' },
  });
  check(response, {
    'isolated write succeeds': (item) => item.status === 200,
  });
}

function dashboardTraffic(baseUrl, profile, cookieHeader) {
  const response = http.get(`${baseUrl}/`, {
    tags: { profile, traffic: 'dashboard' },
    headers: { Cookie: cookieHeader },
  });
  check(response, {
    'dashboard request succeeds': (item) => item.status === 200,
  });
}
