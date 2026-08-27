# Public deployment verification

## Verified target

- URL: <https://fieldops-portfolio.onrender.com>
- Application hosting: Render Free, Frankfurt
- Database: Neon Free PostgreSQL 17, AWS EU Central 1
- Initial verified application revision: `1c3ea75bd9a2df7000d8fa566c791a86a1779edf`
- Verification date: 2026-08-27 JST

## Evidence

| Check                                         | Result                                                                                                                 |
| --------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------- |
| GitHub CI                                     | PASS — formatting, Release build, 62 domain tests, 188 integration tests, container smoke, and 16 Playwright E2E tests |
| `/health/live`                                | HTTP 200                                                                                                               |
| `/health/ready`                               | HTTP 200 with Neon connectivity                                                                                        |
| HTTPS login page                              | PASS                                                                                                                   |
| System Administrator login                    | PASS                                                                                                                   |
| Branch Manager login                          | PASS                                                                                                                   |
| Sales Representative login                    | PASS                                                                                                                   |
| Field Technician login and work-order journey | PASS                                                                                                                   |
| Guarded administrator demo reset              | PASS — approved fictional dataset restored                                                                             |
| Role-specific navigation                      | PASS in a real Chromium browser                                                                                        |

The successful CI run is available at <https://github.com/watawatan1984/FieldOps-Portfolio/actions/runs/33045627531>.

## Free-tier behavior

- Render Free spins down after inactivity. A cold request can take 50 seconds or more.
- Neon Free scales compute to zero while idle, so the first database-backed request can also be slower.
- The public deployment is a portfolio demonstration, not a production SLA target.
- Baseline and stress load tests are intentionally restricted to the isolated local Docker environment.
