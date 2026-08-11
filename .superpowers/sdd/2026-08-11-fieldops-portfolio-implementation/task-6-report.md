# Task 6 Report: Transactional Writes, Audit, Diagnostics, and Health

## Status

Complete. Normal mutations now execute inside an EF Core transaction, acquire PostgreSQL shared advisory transaction lock `4601101` before the business action, save business and audit changes together, and commit atomically. The web host now provides validated correlation IDs, structured JSON logging, safe exception mapping, and separate liveness/readiness endpoints.

## Files

- `src/FieldOps.Features/Abstractions/IAuditWriter.cs`
- `src/FieldOps.Features/Abstractions/ICurrentUser.cs`
- `src/FieldOps.Features/Abstractions/IMutationExecutor.cs`
- `src/FieldOps.Infrastructure/Auditing/AuditWriter.cs`
- `src/FieldOps.Infrastructure/DependencyInjection.cs`
- `src/FieldOps.Infrastructure/Persistence/MutationExecutor.cs`
- `src/FieldOps.Web/Middleware/CorrelationIdMiddleware.cs`
- `src/FieldOps.Web/Middleware/RequestLoggingMiddleware.cs`
- `src/FieldOps.Web/Program.cs`
- `src/FieldOps.Web/Services/HttpCurrentUser.cs`
- `src/FieldOps.Web/Services/PostgresReadinessHealthCheck.cs`
- `tests/FieldOps.IntegrationTests/Diagnostics/DiagnosticsProbeController.cs`
- `tests/FieldOps.IntegrationTests/Diagnostics/DiagnosticsTests.cs`
- `tests/FieldOps.IntegrationTests/Infrastructure/FieldOpsWebApplicationFactory.cs`
- `tests/FieldOps.IntegrationTests/Persistence/MutationExecutorTests.cs`

No package was added. Existing PostgreSQL, EF Core, ASP.NET Core logging, exception handler, and health-check components are used.

## RED / GREEN Evidence

### Transaction and audit

- RED: `MutationExecutorTests` initially failed to compile because `MutationExecutor`, `AuditWriter`, and `ICurrentUser` did not exist.
- GREEN: 2/2 initial real-PostgreSQL transaction tests passed after the minimal implementation.
- RED: the DB diagnostics test failed with `Assert.Single() Failure: The collection was empty` after mutation logging was deliberately removed.
- GREEN: 3/3 mutation tests passed after structured mutation timing was implemented.

### Diagnostics, errors, logging, and health

- RED: all 8 initial diagnostics cases failed at the first missing boundary because `ICurrentUser` was not registered and `IAuditWriter` could not be constructed.
- GREEN: correlation, structured/redacted logging, error mapping, liveness, and readiness reached 8/8.
- RED: direct authorization and missing-resource exception probes returned 500 before explicit mappings (`expected Forbidden, actual InternalServerError`).
- GREEN: `UnauthorizedAccessException` maps to 403 and `KeyNotFoundException` maps to 404.
- RED: the forced request-log regression produced `Outcome=success` for a 500 response.
- GREEN: the same route records `Outcome=failure`, status 500, and the returned correlation ID.
- Final focused verification: 11/11 passed.

## Exact SQL and Lock Evidence

Normal mutation SQL, executed immediately after `BeginTransactionAsync` and before the supplied action:

```sql
SELECT pg_advisory_xact_lock_shared(4601101)
```

The order test used a separate PostgreSQL transaction holding:

```sql
SELECT pg_advisory_xact_lock(4601101)
```

Observed evidence:

- the mutation backend appeared in `pg_stat_activity` with `wait_event_type = 'Lock'` and `wait_event = 'advisory'`;
- the business action had not started while the exclusive lock was held;
- after the exclusive lock committed, the action started inside an active EF transaction;
- `pg_locks` showed the mutation session held a granted advisory `ShareLock` with `classid = 0` and `objid = 4601101`;
- the business row committed only after the shared lock was acquired.

Rollback evidence used a business `Branch` and its `AuditEntry`, called `SaveChangesAsync` inside the mutation, then threw. A new DbContext found neither row, proving the write and audit entry rolled back together. Existing append-only database triggers were unchanged.

## Logging and Redaction Evidence

- Console output uses built-in `AddJsonConsole` with structured message-template fields.
- Request logs contain `CorrelationId`, authenticated non-email Identity `UserId`, `Role`, path-only `Route`, `StatusCode`, `ElapsedMs`, `Operation`, and `Outcome`.
- Mutation logs contain `Operation`, `Outcome`, and `DbElapsedMs`.
- All captured log categories were checked, not only the custom middleware category.
- Logs did not contain the test password, authentication-cookie value, connection string, raw or URL-encoded email address, raw or URL-encoded telephone number, query string, request body, or the generic exception's secret message.
- Exception diagnostics are suppressed after safe handling, and the generic 500 response contains exactly one JSON property: `correlationId`.

## Correlation, Error, and Health Results

- Correlation IDs matching `[A-Za-z0-9._-]{1,64}` are returned unchanged, including the 64-character boundary.
- IDs with spaces, `/`, or 65 characters are replaced by a generated safe ID and returned through `X-Correlation-ID`.
- `DomainException` -> 400.
- `DbUpdateConcurrencyException` -> 409.
- authorization result and `UnauthorizedAccessException` -> 403.
- missing result and `KeyNotFoundException` -> 404.
- unhandled exception -> 500 with correlation ID only.
- `/health/live` remained 200 throughout the test.
- `/health/ready` returned 200 against the real PostgreSQL container with no pending migration, then 503 after the latest migration-history row was removed; `/health/live` remained 200. The readiness check calls `CanConnectAsync` before checking `GetPendingMigrationsAsync`.

## Full Verification

- Focused Release tests: 11/11 passed.
- Full Release suite: 98/98 passed (`FieldOps.Domain.Tests` 57, `FieldOps.IntegrationTests` 39, `FieldOps.E2ETests` 2).
- Release build: succeeded with 0 warnings and 0 errors.
- Task-6-only `dotnet format --verify-no-changes`: passed.
- `git diff --check`: passed.

## Commit

`COMMIT_SHA_PENDING` - `Add transactional diagnostics and health`

## Concerns

- Repository-wide format verification still reports pre-existing line-ending/final-newline/import-order findings in files outside Task 6. Task 6 files pass the scoped format gate; unrelated files were not modified.
- Future CRUD handlers must route normal writes through `IMutationExecutor` and add their success audit entry through `IAuditWriter` inside that action. Task 6 provides and verifies the boundary but intentionally does not implement feature CRUD.
