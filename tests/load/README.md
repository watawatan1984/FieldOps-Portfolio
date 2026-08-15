# FieldOps load tests

These scripts run only against a local `Development` or `LoadTest` FieldOps instance. The runner rejects non-loopback targets before Docker or k6 work starts.

Profiles:

- `baseline`: 20 VUs for 10 minutes, p95 <= 1000 ms.
- `stress`: 100 VUs for 5 minutes, p95 <= 2000 ms.

Setup verifies one-click login for all four demo roles. The reusable authenticated session is used only for read and dashboard traffic; writes use isolated local-only VU-seeded records through the `LoadTest` diagnostics surface.

Traffic mix is deterministic by iteration bucket: 70% reads, 20% isolated writes, 10% dashboard reads.
