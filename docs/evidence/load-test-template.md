# Load Test Evidence Template

## Run Metadata

- Timestamp UTC:
- Git SHA:
- Target:
- k6 image:
- k6 image ID:
- k6 repo digest:
- PostgreSQL image:
- PostgreSQL image ID:

## Profile Results

| Profile | VUs | Duration | Status | HTTP requests | Iterations | p50 ms | p95 ms | p99 ms | Failed checks | HTTP failed rate | Active resets | Integrity |
| --- | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| baseline | 20 | 10m |  |  |  |  |  |  |  |  |  |  |
| stress | 100 | 5m |  |  |  |  |  |  |  |  |  |  |

## Required Checks

- Baseline threshold: p95 <= 1000 ms.
- Stress threshold: p95 <= 2000 ms.
- `checks` rate is 1.0.
- `http_req_failed` rate is 0.
- Postflight integrity is PASS.
- Active reset count is 0.
- Evidence contains no cookies, connection strings, passwords, or request bodies.

## Sanitized Artifacts

- Baseline sanitized summary:
- Stress sanitized summary:
- Baseline raw JSONL:
- Stress raw JSONL:
