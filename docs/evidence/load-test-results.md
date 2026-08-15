# FieldOps Load Test Results

Generated: 2026-08-15T07:05:00Z

These results were captured from the final Task 15 runner scripts against a loopback-only local Kestrel target and disposable local PostgreSQL container. No public Koyeb or non-loopback target was used.

## Run Metadata

| Field | Baseline | Stress |
| --- | --- | --- |
| Run timestamp UTC | 2026-08-15T06:54:40Z | 2026-08-15T06:48:02Z |
| Git SHA | `82c098ba66f3c1e7c7504ace55013236bbefd75a` | `82c098ba66f3c1e7c7504ace55013236bbefd75a` |
| Target | `http://127.0.0.1:5085` | `http://127.0.0.1:5085` |
| k6 image | `grafana/k6:2.2.0` | `grafana/k6:2.2.0` |
| k6 image ID | `sha256:9bd01d6941fca969cb61bb57d2da5ee9b385fe2aa8881df3798c196564d6ace6` | `sha256:9bd01d6941fca969cb61bb57d2da5ee9b385fe2aa8881df3798c196564d6ace6` |
| k6 repo digest | `grafana/k6@sha256:9bd01d6941fca969cb61bb57d2da5ee9b385fe2aa8881df3798c196564d6ace6` | `grafana/k6@sha256:9bd01d6941fca969cb61bb57d2da5ee9b385fe2aa8881df3798c196564d6ace6` |
| PostgreSQL image | `postgres:17-alpine` | `postgres:17-alpine` |
| PostgreSQL image ID | `sha256:d4bb0a8c1b7bb2e29f976d099e7bfb9a5d8858cffe9e46b35cd302cd1f1f8168` | `sha256:d4bb0a8c1b7bb2e29f976d099e7bfb9a5d8858cffe9e46b35cd302cd1f1f8168` |

## Profile Results

| Profile | VUs | Duration | Status | HTTP requests | Iterations | p50 ms | p95 ms | p99 ms | Failed checks | HTTP failed rate | Exceptions | Active resets | Integrity |
| --- | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| baseline | 20 | 10m | PASS | 11843 | 11833 | 11.45 | 31.90 | 79.23 | 0 | 0 | 0 | 0 | PASS |
| stress | 100 | 5m | PASS | 29548 | 29538 | 12.13 | 39.63 | 85.68 | 0 | 0 | 0 | 0 | PASS |

## Status Distribution

| Profile | HTTP 200 | HTTP 302 | Other statuses |
| --- | ---: | ---: | ---: |
| baseline | 11839 | 4 | 0 |
| stress | 29544 | 4 | 0 |

The four HTTP 302 responses in each profile are the setup-only demo role login redirects. Shared authenticated traffic is limited to reads and dashboard requests; isolated writes use deterministic VU-specific seeded records through the LoadTest-only endpoint.

## Preflight And Postflight Evidence

| Profile | Seeded VUs | Role login ready | Branches | Parties | Party roles | Assignments | Sites | Users | Reset count | Active reset count | Orphaned rows |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline | 20 | true | 5 | 60 | 80 | 60 | 60 | 4 | 0 | 0 | 0 |
| stress | 100 | true | 5 | 140 | 160 | 140 | 140 | 4 | 0 | 0 | 0 |

Other deterministic counts after both profiles: contacts 40, sales opportunities 30, work orders 80, work events 250, audit entries 20, demo reset executions 0.

## Required Check Results

- Baseline threshold: p95 31.90 ms <= 1000 ms.
- Stress threshold: p95 39.63 ms <= 2000 ms.
- `checks` rate: 1.0 for both profiles.
- `http_req_failed` rate: 0 for both profiles.
- Failed checks: 0 for both profiles.
- Exception count: 0 for both profiles.
- Postflight FK integrity: PASS for both profiles.
- Active reset count: 0 for both profiles.
- Evidence files were scanned for cookies, connection strings, passwords, and request bodies before commit.

## Sanitized Artifacts

- Baseline sanitized summary: `artifacts/load/20260815T065440Z-baseline/baseline-sanitized-summary.json`
- Stress sanitized summary: `artifacts/load/20260815T064802Z-stress/stress-sanitized-summary.json`
- Baseline redacted k6 summary: `artifacts/load/20260815T065440Z-baseline/baseline-summary.json`
- Stress redacted k6 summary: `artifacts/load/20260815T064802Z-stress/stress-summary.json`
- Baseline raw JSONL status metrics: `artifacts/load/20260815T065440Z-baseline/baseline-raw.jsonl`
- Stress raw JSONL status metrics: `artifacts/load/20260815T064802Z-stress/stress-raw.jsonl`
