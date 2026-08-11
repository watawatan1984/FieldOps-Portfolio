# FieldOps Portal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and publicly demonstrate a fictional, production-shaped C#/.NET 10 internal operations portal with four roles, PostgreSQL persistence, safe on-demand demo reset, role-specific E2E coverage, structured diagnostics, and reproducible load-test evidence.

**Architecture:** Use a modular monolith with ASP.NET Core MVC and Razor Views at the web boundary, feature-oriented application services, a dependency-free domain model, and an EF Core/Npgsql infrastructure layer. All normal writes run inside a transaction that takes a PostgreSQL shared advisory lock; reset takes the matching exclusive advisory lock so it cannot race normal mutations. A controlled local Docker environment is the performance-test target, while GitHub, Neon, and Koyeb provide the public source and interactive demo.

**Tech Stack:** C# 14, .NET 10.0.10, ASP.NET Core MVC, Razor Views, Bootstrap 5, EF Core 10.0.10, Npgsql 10.0.3, PostgreSQL 17, ASP.NET Core Identity, Docker Compose, xUnit 2.9.3, Testcontainers.PostgreSql 4.13.0, Microsoft Playwright 1.61.0, k6 2.2.0, GitHub Actions, Neon Postgres, Koyeb.

## Global Constraints

- Use only fictional names, addresses, email addresses, telephone numbers, work descriptions, and identifiers.
- Present the repository as a portfolio reconstruction, never as source code taken from an employer or client.
- The visible application name is `FieldOps Portal`; the header reset button text is exactly `初期化`.
- The four demo roles are System Administrator, Branch Manager, Sales Representative, and Field Technician.
- Only System Administrator can open or execute demo reset.
- Store timestamps as UTC and format them in `Asia/Tokyo` for the UI.
- Keep the modular-monolith boundary: Domain depends on no other project; Features depends on Domain; Infrastructure depends on Domain and Features; Web composes all projects.
- Do not add paid services. GitHub, Neon, and Koyeb must remain within their free plans.
- Do not run the 20-user or 100-user load profiles against the public free Koyeb instance. Run them against the controlled Docker Compose stack.
- Do not log passwords, authentication cookies, bearer tokens, connection strings, request bodies, or personal fields.
- Do not claim “bug-free.” Completion means all declared quality gates pass and the declared test matrix contains zero known open defects.
- Do not publish, create cloud resources, or push to a new remote until the exact GitHub repository, Neon project, Koyeb app/service, visibility, and rollback path have been shown to the user immediately before that external action.

---

## Planned File Structure

```text
FieldOps-Portfolio/
├─ FieldOps.sln
├─ global.json
├─ Directory.Build.props
├─ Directory.Packages.props
├─ .editorconfig
├─ .dockerignore
├─ Dockerfile
├─ compose.yaml
├─ README.md
├─ src/
│  ├─ FieldOps.Domain/
│  │  ├─ Common/Entity.cs
│  │  ├─ Common/DomainException.cs
│  │  ├─ Entities/Branch.cs
│  │  ├─ Entities/Party.cs
│  │  ├─ Entities/PartyRole.cs
│  │  ├─ Entities/PartyBranchAssignment.cs
│  │  ├─ Entities/Contact.cs
│  │  ├─ Entities/Site.cs
│  │  ├─ Entities/SalesOpportunity.cs
│  │  ├─ Entities/WorkOrder.cs
│  │  ├─ Entities/WorkEvent.cs
│  │  ├─ Entities/AuditEntry.cs
│  │  └─ Enums/*.cs
│  ├─ FieldOps.Features/
│  │  ├─ Abstractions/IFieldOpsDbContext.cs
│  │  ├─ Abstractions/ICurrentUser.cs
│  │  ├─ Abstractions/IMutationExecutor.cs
│  │  ├─ Abstractions/IAuditWriter.cs
│  │  ├─ Parties/*.cs
│  │  ├─ Sales/*.cs
│  │  ├─ Work/*.cs
│  │  ├─ Dashboard/*.cs
│  │  └─ Administration/*.cs
│  ├─ FieldOps.Infrastructure/
│  │  ├─ Persistence/FieldOpsDbContext.cs
│  │  ├─ Persistence/Configurations/*.cs
│  │  ├─ Persistence/Migrations/*.cs
│  │  ├─ Persistence/MutationExecutor.cs
│  │  ├─ Identity/ApplicationUser.cs
│  │  ├─ Identity/DemoIdentitySeeder.cs
│  │  ├─ Auditing/AuditWriter.cs
│  │  ├─ Demo/DemoDataSeeder.cs
│  │  ├─ Demo/DemoResetService.cs
│  │  └─ DependencyInjection.cs
│  └─ FieldOps.Web/
│     ├─ Program.cs
│     ├─ Controllers/*.cs
│     ├─ Authorization/Policies.cs
│     ├─ Middleware/CorrelationIdMiddleware.cs
│     ├─ Middleware/RequestLoggingMiddleware.cs
│     ├─ Models/*.cs
│     ├─ Views/**/*.cshtml
│     └─ wwwroot/{css,js}/site.*
├─ tests/
│  ├─ FieldOps.Domain.Tests/
│  ├─ FieldOps.IntegrationTests/
│  ├─ FieldOps.E2ETests/
│  └─ load/
│     ├─ baseline.js
│     ├─ stress.js
│     └─ README.md
├─ docs/
│  ├─ architecture.md
│  ├─ operations.md
│  ├─ testing.md
│  ├─ evidence/
│  └─ superpowers/
└─ .github/workflows/{ci.yml,release.yml}
```

The planned public API is intentionally small. MVC controllers call application services; they do not issue EF Core queries directly. Infrastructure supplies these application boundaries:

```csharp
public interface IFieldOpsDbContext
{
    IQueryable<Branch> Branches { get; }
    IQueryable<Party> Parties { get; }
    IQueryable<SalesOpportunity> SalesOpportunities { get; }
    IQueryable<WorkOrder> WorkOrders { get; }
    IQueryable<WorkEvent> WorkEvents { get; }
    IQueryable<AuditEntry> AuditEntries { get; }
    void Add<TEntity>(TEntity entity) where TEntity : class;
    void RemoveRange<TEntity>(IEnumerable<TEntity> entities) where TEntity : class;
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IMutationExecutor
{
    Task<T> ExecuteAsync<T>(
        string operation,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken);
}

public interface ICurrentUser
{
    string UserId { get; }
    Guid BranchId { get; }
    string Role { get; }
}

public interface IAuditWriter
{
    void Add(
        string action,
        string entityType,
        string entityId,
        string outcome,
        object? changeSummary = null);
}
```

---

### Task 1: Scaffold the solution and lock dependency versions

**Files:**
- Create: `FieldOps.sln`
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: `.editorconfig`
- Create: `src/FieldOps.Domain/FieldOps.Domain.csproj`
- Create: `src/FieldOps.Features/FieldOps.Features.csproj`
- Create: `src/FieldOps.Infrastructure/FieldOps.Infrastructure.csproj`
- Create: `src/FieldOps.Web/FieldOps.Web.csproj`
- Create: `tests/FieldOps.Domain.Tests/FieldOps.Domain.Tests.csproj`
- Create: `tests/FieldOps.IntegrationTests/FieldOps.IntegrationTests.csproj`
- Create: `tests/FieldOps.E2ETests/FieldOps.E2ETests.csproj`

- [ ] **Step 1: Pin the installed .NET 10 SDK and repository-wide compiler settings**

Create `global.json` with SDK `10.0.110`, roll-forward `latestPatch`, and prerelease disabled. Create `Directory.Build.props` with `net10.0`, nullable enabled, implicit usings enabled, warnings treated as errors, invariant globalization disabled, and deterministic builds enabled.

- [ ] **Step 2: Create the four production projects and three test projects**

Run:

```powershell
dotnet new sln -n FieldOps --format sln
dotnet new classlib -n FieldOps.Domain -o src/FieldOps.Domain -f net10.0
dotnet new classlib -n FieldOps.Features -o src/FieldOps.Features -f net10.0
dotnet new classlib -n FieldOps.Infrastructure -o src/FieldOps.Infrastructure -f net10.0
dotnet new mvc -n FieldOps.Web -o src/FieldOps.Web -f net10.0 --auth None
dotnet new xunit -n FieldOps.Domain.Tests -o tests/FieldOps.Domain.Tests -f net10.0
dotnet new xunit -n FieldOps.IntegrationTests -o tests/FieldOps.IntegrationTests -f net10.0
dotnet new xunit -n FieldOps.E2ETests -o tests/FieldOps.E2ETests -f net10.0
dotnet sln FieldOps.sln add (Get-ChildItem src,tests -Recurse -Filter *.csproj).FullName
```

Expected: seven projects are added and `dotnet sln FieldOps.sln list` reports all seven.

- [ ] **Step 3: Add project references and centrally managed packages**

Dependency edges:

```text
FieldOps.Features       -> FieldOps.Domain
FieldOps.Infrastructure -> FieldOps.Domain, FieldOps.Features
FieldOps.Web            -> FieldOps.Domain, FieldOps.Features, FieldOps.Infrastructure
Domain.Tests            -> FieldOps.Domain
IntegrationTests        -> all production projects
E2ETests                -> FieldOps.Web
```

Pin these package versions in `Directory.Packages.props`:

```xml
<PackageVersion Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" Version="10.0.10" />
<PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.10" />
<PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.10" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.10" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.10" />
<PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
<PackageVersion Include="Testcontainers.PostgreSql" Version="4.13.0" />
<PackageVersion Include="Microsoft.Playwright" Version="1.61.0" />
<PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />
<PackageVersion Include="xunit" Version="2.9.3" />
<PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
<PackageVersion Include="coverlet.collector" Version="10.0.1" />
```

- [ ] **Step 4: Add an architecture dependency test before feature code**

Create `tests/FieldOps.Domain.Tests/Architecture/ProjectDependencyTests.cs`. Parse each project file and assert that Domain has no project reference, Features references only Domain, and Web does not reference EF Core packages directly.

Run before fixing generated references:

```powershell
dotnet test tests/FieldOps.Domain.Tests --filter ProjectDependencyTests
```

Expected initial result: FAIL because the test has not yet been satisfied.

- [ ] **Step 5: Make the dependency test pass and establish the baseline**

Run:

```powershell
dotnet restore FieldOps.sln
dotnet build FieldOps.sln --configuration Release --no-restore
dotnet test FieldOps.sln --configuration Release --no-build
```

Expected: restore, build, and generated baseline tests pass with zero warnings.

- [ ] **Step 6: Commit the foundation**

```powershell
git add FieldOps.sln global.json Directory.Build.props Directory.Packages.props .editorconfig src tests
git commit -m "Build FieldOps solution foundation"
```

---

### Task 2: Implement the branch and party domain model

**Files:**
- Create: `src/FieldOps.Domain/Common/Entity.cs`
- Create: `src/FieldOps.Domain/Common/DomainException.cs`
- Create: `src/FieldOps.Domain/Entities/Branch.cs`
- Create: `src/FieldOps.Domain/Entities/Party.cs`
- Create: `src/FieldOps.Domain/Entities/PartyRole.cs`
- Create: `src/FieldOps.Domain/Entities/PartyBranchAssignment.cs`
- Create: `src/FieldOps.Domain/Entities/Contact.cs`
- Create: `src/FieldOps.Domain/Entities/Site.cs`
- Create: `src/FieldOps.Domain/Enums/PartyRoleType.cs`
- Test: `tests/FieldOps.Domain.Tests/Parties/PartyTests.cs`

- [ ] **Step 1: Write failing tests for party identity and role reuse**

Cover these behaviors:

```csharp
[Fact]
public void AddRole_AllowsCustomerAndBusinessPartnerOnOneParty()
{
    var party = Party.CreateOrganization("東都設備株式会社");

    party.AddRole(PartyRoleType.Customer);
    party.AddRole(PartyRoleType.BusinessPartner);

    Assert.Equal(2, party.Roles.Count);
}

[Fact]
public void AddRole_RejectsDuplicateRole()
{
    var party = Party.CreateOrganization("東都設備株式会社");
    party.AddRole(PartyRoleType.Customer);

    Assert.Throws<DomainException>(() => party.AddRole(PartyRoleType.Customer));
}
```

Also test trimmed required names, duplicate branch assignments, primary contact uniqueness, and a site requiring an assigned branch.

Run:

```powershell
dotnet test tests/FieldOps.Domain.Tests --filter "FullyQualifiedName~PartyTests"
```

Expected initial result: FAIL because the domain types do not exist.

- [ ] **Step 2: Implement entity identity and optimistic-concurrency fields**

`Entity` exposes `Guid Id`, `DateTime CreatedAtUtc`, `DateTime UpdatedAtUtc`, and `uint Version`. Constructors validate required text and keep collections private with read-only views.

- [ ] **Step 3: Implement party roles, branches, contacts, and sites**

Use one `Party` record for an organization or person. A party can own both Customer and BusinessPartner roles. `PartyBranchAssignment` controls which branches can access it; Branch Manager and branch-scoped users cannot infer access from free-form address data.

- [ ] **Step 4: Run domain tests and full build**

```powershell
dotnet test tests/FieldOps.Domain.Tests --filter "FullyQualifiedName~Parties"
dotnet build FieldOps.sln --configuration Release
```

Expected: all party tests pass and the solution builds with zero warnings.

- [ ] **Step 5: Commit the party model**

```powershell
git add src/FieldOps.Domain tests/FieldOps.Domain.Tests
git commit -m "Model branches and reusable parties"
```

---

### Task 3: Implement sales and work-order state machines

**Files:**
- Create: `src/FieldOps.Domain/Entities/SalesOpportunity.cs`
- Create: `src/FieldOps.Domain/Entities/WorkOrder.cs`
- Create: `src/FieldOps.Domain/Entities/WorkEvent.cs`
- Create: `src/FieldOps.Domain/Entities/AuditEntry.cs`
- Create: `src/FieldOps.Domain/Enums/SalesOpportunityStatus.cs`
- Create: `src/FieldOps.Domain/Enums/WorkOrderStatus.cs`
- Create: `src/FieldOps.Domain/Enums/WorkEventType.cs`
- Test: `tests/FieldOps.Domain.Tests/Sales/SalesOpportunityTests.cs`
- Test: `tests/FieldOps.Domain.Tests/Work/WorkOrderTests.cs`

- [ ] **Step 1: Write failing transition-table tests**

Sales transitions:

```text
New -> Contacted -> SurveyScheduled -> Quoting -> Proposed -> Won
New|Contacted|SurveyScheduled|Quoting|Proposed -> Lost
New|Contacted|SurveyScheduled|Quoting|Proposed -> OnHold
OnHold -> Contacted|SurveyScheduled|Quoting|Proposed|Lost
```

Work transitions:

```text
Planned -> Scheduled -> InProgress -> Completed
Planned|Scheduled|InProgress -> Cancelled
```

Use xUnit `TheoryData` for allowed and rejected transitions. Test that Won requires an amount and expected close date, Completed requires at least one completion event, and terminal states reject further transitions.

Run:

```powershell
dotnet test tests/FieldOps.Domain.Tests --filter "FullyQualifiedName~SalesOpportunityTests|FullyQualifiedName~WorkOrderTests"
```

Expected initial result: FAIL because the aggregates and transition methods do not exist.

- [ ] **Step 2: Implement explicit transition methods**

Expose `MoveTo(SalesOpportunityStatus next, DateTime occurredAtUtc)` and `MoveTo(WorkOrderStatus next, DateTime occurredAtUtc)`. Invalid moves throw `DomainException` containing the aggregate type, current state, and requested state without personal data.

- [ ] **Step 3: Implement append-only work events and audit entries**

`WorkEvent` stores event type, occurred timestamp, branch, non-sensitive summary, and actor ID. Updates append a new event rather than rewriting historical events. `AuditEntry` is immutable after construction.

- [ ] **Step 4: Run tests and mutation coverage**

```powershell
dotnet test tests/FieldOps.Domain.Tests --configuration Release --collect:"XPlat Code Coverage"
```

Expected: transition tests pass; Domain line coverage is at least 90% and branch coverage is at least 85% for state-machine files.

- [ ] **Step 5: Commit state machines**

```powershell
git add src/FieldOps.Domain tests/FieldOps.Domain.Tests
git commit -m "Model sales and work lifecycles"
```

---

### Task 4: Add PostgreSQL persistence and real-database integration tests

**Files:**
- Create: `src/FieldOps.Features/Abstractions/IFieldOpsDbContext.cs`
- Create: `src/FieldOps.Infrastructure/Persistence/FieldOpsDbContext.cs`
- Create: `src/FieldOps.Infrastructure/Persistence/Configurations/*.cs`
- Create: `src/FieldOps.Infrastructure/Persistence/Migrations/*_InitialCreate.cs`
- Create: `src/FieldOps.Infrastructure/DependencyInjection.cs`
- Create: `tests/FieldOps.IntegrationTests/Infrastructure/PostgresFixture.cs`
- Create: `tests/FieldOps.IntegrationTests/Infrastructure/DatabaseCollection.cs`
- Test: `tests/FieldOps.IntegrationTests/Persistence/ModelMappingTests.cs`

- [ ] **Step 1: Write failing PostgreSQL model tests**

The tests start `postgres:17-alpine` with Testcontainers and assert:

- migrations apply to an empty database;
- a Party with two roles and two branch assignments round-trips;
- `uint Version` changes after an update and stale writes raise `DbUpdateConcurrencyException`;
- a WorkEvent cannot be deleted through the configured DbContext;
- all timestamps remain UTC after round-trip.

Run:

```powershell
dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~ModelMappingTests"
```

Expected initial result: FAIL because the DbContext and migration do not exist.

- [ ] **Step 2: Implement EF Core mappings and indexes**

Required indexes:

```text
Party.NormalizedName
PartyRole (PartyId, RoleType) unique
PartyBranchAssignment (PartyId, BranchId) unique
SalesOpportunity (BranchId, Status, ExpectedCloseDate)
WorkOrder (BranchId, Status, ScheduledStartUtc)
WorkOrder (PartyId, SiteId)
WorkEvent (WorkOrderId, OccurredAtUtc desc)
AuditEntry (OccurredAtUtc desc, ActorUserId)
```

Map `Version` with Npgsql row-version semantics. Configure delete behavior explicitly: historical WorkEvent and AuditEntry rows use Restrict; aggregate-owned transient records use Cascade only where the design permits reset.

- [ ] **Step 3: Generate and inspect the initial migration**

```powershell
dotnet ef migrations add InitialCreate --project src/FieldOps.Infrastructure --startup-project src/FieldOps.Web --output-dir Persistence/Migrations
dotnet ef migrations script --idempotent --project src/FieldOps.Infrastructure --startup-project src/FieldOps.Web --output docs/evidence/initial-migration.sql
```

Expected: the script contains tables, foreign keys, unique constraints, indexes, and no destructive drop of an existing production table.

- [ ] **Step 4: Pass the integration suite**

```powershell
dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~Persistence"
```

Expected: all tests pass against PostgreSQL, not an in-memory provider.

- [ ] **Step 5: Commit persistence**

```powershell
git add src/FieldOps.Features src/FieldOps.Infrastructure tests/FieldOps.IntegrationTests docs/evidence/initial-migration.sql
git commit -m "Persist FieldOps data in PostgreSQL"
```

---

### Task 5: Add Identity, four demo users, and authorization policies

**Files:**
- Create: `src/FieldOps.Infrastructure/Identity/ApplicationUser.cs`
- Create: `src/FieldOps.Infrastructure/Identity/DemoIdentitySeeder.cs`
- Create: `src/FieldOps.Web/Authorization/Policies.cs`
- Create: `src/FieldOps.Web/Authorization/BranchAccessHandler.cs`
- Create: `src/FieldOps.Web/Controllers/DemoLoginController.cs`
- Create: `src/FieldOps.Web/Views/DemoLogin/Index.cshtml`
- Create: `src/FieldOps.Web/Models/DemoRoleCardViewModel.cs`
- Test: `tests/FieldOps.IntegrationTests/Authorization/AuthorizationPolicyTests.cs`
- Test: `tests/FieldOps.IntegrationTests/Authorization/DemoLoginTests.cs`

- [ ] **Step 1: Write failing policy matrix tests**

Assert this matrix at the HTTP boundary:

| Capability | SysAdmin | Branch Manager | Sales | Technician |
|---|---:|---:|---:|---:|
| View dashboard | Yes | Own branch | Own branch | Own assignments |
| Manage parties | Yes | Own branch | Own branch | No |
| Manage sales | Yes | Own branch | Own branch | Read assigned |
| Manage work orders | Yes | Own branch | Read own branch | Update assigned |
| View audit | Yes | Own branch | No | No |
| Reset demo | Yes | No | No | No |

Tests must cover both hidden navigation and direct URL requests. A forbidden direct request returns 403; an unauthenticated request redirects to the demo login page.

- [ ] **Step 2: Configure Identity cookies and stable demo accounts**

Seed fixed usernames by role but generate strong non-public passwords at startup. One-click login posts a signed role choice to the server; the browser never receives or submits the password. Configure secure, HTTP-only, same-site cookies and a 30-minute idle expiration.

- [ ] **Step 3: Implement branch and assignment resource authorization**

Branch scope is derived from authenticated claims and database assignments. Ignore any submitted BranchId that exceeds the user scope. The handler evaluates the loaded resource before the controller calls a mutation service.

- [ ] **Step 4: Run authorization tests**

```powershell
dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~Authorization|FullyQualifiedName~DemoLogin"
```

Expected: every allowed case returns 2xx/3xx as designed and every denied direct request returns 403 without altering data.

- [ ] **Step 5: Commit identity and policies**

```powershell
git add src/FieldOps.Infrastructure/Identity src/FieldOps.Web/Authorization src/FieldOps.Web/Controllers/DemoLoginController.cs src/FieldOps.Web/Views/DemoLogin src/FieldOps.Web/Models tests/FieldOps.IntegrationTests/Authorization
git commit -m "Authorize four FieldOps demo roles"
```

---

### Task 6: Establish transactional writes, audit, diagnostics, and health

**Files:**
- Create: `src/FieldOps.Features/Abstractions/IMutationExecutor.cs`
- Create: `src/FieldOps.Features/Abstractions/IAuditWriter.cs`
- Create: `src/FieldOps.Features/Abstractions/ICurrentUser.cs`
- Create: `src/FieldOps.Infrastructure/Persistence/MutationExecutor.cs`
- Create: `src/FieldOps.Infrastructure/Auditing/AuditWriter.cs`
- Create: `src/FieldOps.Web/Services/HttpCurrentUser.cs`
- Create: `src/FieldOps.Web/Middleware/CorrelationIdMiddleware.cs`
- Create: `src/FieldOps.Web/Middleware/RequestLoggingMiddleware.cs`
- Modify: `src/FieldOps.Web/Program.cs`
- Test: `tests/FieldOps.IntegrationTests/Diagnostics/DiagnosticsTests.cs`
- Test: `tests/FieldOps.IntegrationTests/Persistence/MutationExecutorTests.cs`

- [ ] **Step 1: Write failing transaction and diagnostic tests**

Tests assert:

- every normal mutation opens a transaction and runs `SELECT pg_advisory_xact_lock_shared(4601101)` before changes;
- an exception rolls back business data and its audit entry;
- `X-Correlation-ID` is accepted only when 1–64 characters match `[A-Za-z0-9._-]+`; otherwise a generated ID is returned;
- logs include correlation ID, user ID, role, route, status code, elapsed milliseconds, operation, and outcome;
- logs do not contain the test password, cookie value, connection string, email address, or telephone number;
- `/health/live` tests process liveness and `/health/ready` tests PostgreSQL connectivity plus pending migrations.

- [ ] **Step 2: Implement the shared-lock mutation executor**

Core order:

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
await db.Database.ExecuteSqlRawAsync(
    "SELECT pg_advisory_xact_lock_shared(4601101)", cancellationToken);
var result = await action(cancellationToken);
await db.SaveChangesAsync(cancellationToken);
await transaction.CommitAsync(cancellationToken);
return result;
```

The reset service in Task 12 uses exclusive `pg_advisory_xact_lock(4601101)` within its own transaction.

- [ ] **Step 3: Add structured JSON logging and failure mapping**

Use built-in `System.Text.Json` console logging. Map `DomainException` to HTTP 400, `DbUpdateConcurrencyException` to HTTP 409, authorization failure to 403, missing resource to 404, and unhandled exceptions to a generic 500 page that includes only the correlation ID.

- [ ] **Step 4: Add health endpoints and test all paths**

```powershell
dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~Diagnostics|FullyQualifiedName~MutationExecutor"
```

Expected: transaction rollback, redaction, correlation, and health tests pass.

- [ ] **Step 5: Commit cross-cutting infrastructure**

```powershell
git add src/FieldOps.Features/Abstractions src/FieldOps.Infrastructure/Auditing src/FieldOps.Infrastructure/Persistence/MutationExecutor.cs src/FieldOps.Web tests/FieldOps.IntegrationTests
git commit -m "Add transactional diagnostics and health"
```

---

### Task 7: Build party, customer, and business-partner screens

**Files:**
- Create: `src/FieldOps.Features/Parties/PartyQueries.cs`
- Create: `src/FieldOps.Features/Parties/PartyCommands.cs`
- Create: `src/FieldOps.Features/Parties/PartyDtos.cs`
- Create: `src/FieldOps.Web/Controllers/PartiesController.cs`
- Create: `src/FieldOps.Web/Controllers/CustomersController.cs`
- Create: `src/FieldOps.Web/Controllers/BusinessPartnersController.cs`
- Create: `src/FieldOps.Web/Views/Parties/{Index,Details,Edit}.cshtml`
- Create: `src/FieldOps.Web/Views/Customers/Index.cshtml`
- Create: `src/FieldOps.Web/Views/BusinessPartners/Index.cshtml`
- Test: `tests/FieldOps.IntegrationTests/Features/PartyFeatureTests.cs`

- [ ] **Step 1: Write failing feature tests**

Cover search by normalized name/contact/site, pagination, role tabs, create/update, adding a second role to an existing party, branch sharing, duplicate conflict, validation, optimistic-concurrency conflict, and cross-branch denial.

- [ ] **Step 2: Implement query objects with bounded result sizes**

Use a default page size of 25 and maximum of 100. Project directly to DTOs with `AsNoTracking`; never materialize every party before filtering.

- [ ] **Step 3: Implement commands through `IMutationExecutor`**

Each successful command adds one audit entry containing action, entity type, entity ID, branch, actor, timestamp, outcome, and a field-name-only change summary. Values of email, telephone, and address fields are not copied into the audit payload.

- [ ] **Step 4: Implement accessible MVC pages**

Use server-side validation summaries, explicit labels, keyboard-visible focus, empty-state text, status badges, breadcrumbs, and branch-scope indicators. Preserve search criteria in paging links.

- [ ] **Step 5: Run tests**

```powershell
dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~PartyFeatureTests"
```

Expected: all CRUD, access-control, concurrency, and query-bound tests pass.

- [ ] **Step 6: Commit the party feature**

```powershell
git add src/FieldOps.Features/Parties src/FieldOps.Web/Controllers src/FieldOps.Web/Views tests/FieldOps.IntegrationTests/Features/PartyFeatureTests.cs
git commit -m "Build party management workflows"
```

---

### Task 8: Build sales opportunity management

**Files:**
- Create: `src/FieldOps.Features/Sales/SalesQueries.cs`
- Create: `src/FieldOps.Features/Sales/SalesCommands.cs`
- Create: `src/FieldOps.Features/Sales/SalesDtos.cs`
- Create: `src/FieldOps.Web/Controllers/SalesController.cs`
- Create: `src/FieldOps.Web/Views/Sales/{Index,Details,Edit}.cshtml`
- Test: `tests/FieldOps.IntegrationTests/Features/SalesFeatureTests.cs`

- [ ] **Step 1: Write failing sales workflow tests**

Test create, edit, status transitions, rejected transitions, amount/date validation, branch scoping, salesperson ownership, manager visibility, technician read-only access to assigned work-derived context, pagination, and 409 on stale version.

- [ ] **Step 2: Implement sales queries and commands**

List filters: branch, owner, status, expected-close range, amount range, and free-text party/site search. Use stable ordering by expected-close date then ID.

- [ ] **Step 3: Build MVC views**

The details page shows the transition actions valid from the current state and a chronological audit summary. Invalid transitions are rejected server-side even if a crafted POST bypasses the page.

- [ ] **Step 4: Run tests**

```powershell
dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~SalesFeatureTests"
```

Expected: all sales tests pass, including direct crafted requests.

- [ ] **Step 5: Commit sales management**

```powershell
git add src/FieldOps.Features/Sales src/FieldOps.Web/Controllers/SalesController.cs src/FieldOps.Web/Views/Sales tests/FieldOps.IntegrationTests/Features/SalesFeatureTests.cs
git commit -m "Build sales opportunity management"
```

---

### Task 9: Build work orders and append-only work history

**Files:**
- Create: `src/FieldOps.Features/Work/WorkOrderQueries.cs`
- Create: `src/FieldOps.Features/Work/WorkOrderCommands.cs`
- Create: `src/FieldOps.Features/Work/WorkOrderDtos.cs`
- Create: `src/FieldOps.Web/Controllers/WorkOrdersController.cs`
- Create: `src/FieldOps.Web/Views/WorkOrders/{Index,Details,Edit,AddEvent}.cshtml`
- Test: `tests/FieldOps.IntegrationTests/Features/WorkOrderFeatureTests.cs`

- [ ] **Step 1: Write failing work-order tests**

Cover create from a Won sales opportunity, scheduling, technician assignment, valid and invalid transitions, completion-event requirement, append-only events, manager scope, technician assignment scope, sales read-only scope, cancellation, and stale-version conflict.

- [ ] **Step 2: Implement commands and queries**

All transitions and event additions run through `IMutationExecutor`. A completed order remains readable but rejects edits other than an administrator-authored correction event that is separately audited.

- [ ] **Step 3: Build the work-order pages**

Details show customer/site, schedule, assigned technician, current state, allowed actions, and a chronological event timeline. Do not expose internal Identity IDs in links or visible text.

- [ ] **Step 4: Run tests**

```powershell
dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~WorkOrderFeatureTests"
```

Expected: all work-order, append-only, and authorization tests pass.

- [ ] **Step 5: Commit work management**

```powershell
git add src/FieldOps.Features/Work src/FieldOps.Web/Controllers/WorkOrdersController.cs src/FieldOps.Web/Views/WorkOrders tests/FieldOps.IntegrationTests/Features/WorkOrderFeatureTests.cs
git commit -m "Build work order workflows"
```

---

### Task 10: Implement multi-condition work-history search

**Files:**
- Create: `src/FieldOps.Features/Work/WorkHistorySearch.cs`
- Create: `src/FieldOps.Web/Controllers/WorkHistoryController.cs`
- Create: `src/FieldOps.Web/Models/WorkHistorySearchViewModel.cs`
- Create: `src/FieldOps.Web/Views/WorkHistory/Index.cshtml`
- Test: `tests/FieldOps.IntegrationTests/Features/WorkHistorySearchTests.cs`

- [ ] **Step 1: Write failing search tests**

Combine these filters in one query: branch, customer, business partner, site, work status, event type, technician, scheduled date range, completion date range, and normalized keyword. Test empty criteria, no matches, multiple criteria, Japanese text, date-boundary inclusivity, unauthorized branch, page stability, and maximum page size.

- [ ] **Step 2: Implement one composable database query**

Apply branch authorization first, then optional predicates, stable sort, count, skip, take, and DTO projection. Use `EF.Functions.ILike` for normalized textual search. Record query duration and result count without logging the raw keyword.

- [ ] **Step 3: Add an explain-plan regression check**

Seed 10,000 work orders locally and run `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` for the broadest declared search. Store the sanitized plan in `docs/evidence/work-history-explain.json`. Fail the verification script if a sequential scan appears on WorkOrders for a branch-plus-date query.

- [ ] **Step 4: Build the search page and run tests**

```powershell
dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~WorkHistorySearchTests"
```

Expected: all search combinations and access-control tests pass.

- [ ] **Step 5: Commit search**

```powershell
git add src/FieldOps.Features/Work/WorkHistorySearch.cs src/FieldOps.Web/Controllers/WorkHistoryController.cs src/FieldOps.Web/Models/WorkHistorySearchViewModel.cs src/FieldOps.Web/Views/WorkHistory tests/FieldOps.IntegrationTests/Features/WorkHistorySearchTests.cs docs/evidence/work-history-explain.json
git commit -m "Add indexed work history search"
```

---

### Task 11: Build dashboard, branch progress, navigation, and audit views

**Files:**
- Create: `src/FieldOps.Features/Dashboard/DashboardQueries.cs`
- Create: `src/FieldOps.Features/Dashboard/BranchProgressQueries.cs`
- Create: `src/FieldOps.Features/Administration/AuditQueries.cs`
- Create: `src/FieldOps.Web/Controllers/HomeController.cs`
- Create: `src/FieldOps.Web/Controllers/BranchesController.cs`
- Create: `src/FieldOps.Web/Controllers/AuditController.cs`
- Create: `src/FieldOps.Web/Views/Home/Index.cshtml`
- Create: `src/FieldOps.Web/Views/Branches/{Index,Details}.cshtml`
- Create: `src/FieldOps.Web/Views/Audit/Index.cshtml`
- Modify: `src/FieldOps.Web/Views/Shared/_Layout.cshtml`
- Modify: `src/FieldOps.Web/wwwroot/css/site.css`
- Test: `tests/FieldOps.IntegrationTests/Features/DashboardTests.cs`

- [ ] **Step 1: Write failing dashboard and navigation tests**

Assert role-specific counts, branch scope, empty-state behavior, audit visibility, query count limits, left-navigation links, active-page marker, responsive menu, and absence of unauthorized links.

- [ ] **Step 2: Implement summary queries**

Dashboard cards: open opportunities, proposals due, scheduled work, work in progress, overdue work, and completions this month. Branch progress shows the same measures by branch only to administrators; Branch Manager sees the own-branch detail without the national comparison.

- [ ] **Step 3: Build the integrated dashboard shell**

Use a persistent left navigation at desktop widths and an off-canvas menu on narrow screens. Header includes current fictional user, role, branch, logout, and—only for System Administrator—the `初期化` button.

- [ ] **Step 4: Run tests and a query-count assertion**

```powershell
dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~DashboardTests"
```

Expected: dashboard tests pass and one dashboard request executes no more than eight SQL commands.

- [ ] **Step 5: Commit dashboard and navigation**

```powershell
git add src/FieldOps.Features/Dashboard src/FieldOps.Features/Administration src/FieldOps.Web/Controllers src/FieldOps.Web/Views src/FieldOps.Web/wwwroot/css/site.css tests/FieldOps.IntegrationTests/Features/DashboardTests.cs
git commit -m "Build dashboard and branch progress views"
```

---

### Task 12: Implement safe on-demand demo reset with loading UI

**Files:**
- Create: `src/FieldOps.Infrastructure/Demo/DemoDataSeeder.cs`
- Create: `src/FieldOps.Infrastructure/Demo/DemoResetExecution.cs`
- Create: `src/FieldOps.Infrastructure/Demo/DemoResetService.cs`
- Create: `src/FieldOps.Infrastructure/Persistence/Migrations/*_AddDemoResetExecution.cs`
- Create: `src/FieldOps.Features/Administration/IDemoResetService.cs`
- Create: `src/FieldOps.Web/Controllers/AdministrationController.cs`
- Create: `src/FieldOps.Web/Views/Administration/Reset.cshtml`
- Create: `src/FieldOps.Web/wwwroot/js/demo-reset.js`
- Modify: `src/FieldOps.Web/Views/Shared/_Layout.cshtml`
- Test: `tests/FieldOps.IntegrationTests/Administration/DemoResetTests.cs`
- Test: `tests/FieldOps.IntegrationTests/Administration/DemoResetConcurrencyTests.cs`

- [ ] **Step 1: Write failing reset behavior tests**

Assert:

- only System Administrator sees and opens `初期化`;
- non-admin direct GET and POST return 403;
- POST requires a valid antiforgery token;
- two confirmation steps are required: open confirmation page, then submit `RESET` as a server-validated confirmation value;
- duplicate submit with the same idempotency key executes once;
- a successful reset restores deterministic row counts and stable demo identifiers;
- a forced failure rolls back all deletions and seed inserts;
- an audit row records start, completion or rollback, duration, actor, and correlation ID;
- normal mutations block during reset and resume afterward without lost writes;
- a second reset waits on the exclusive advisory lock and returns the already-completed result for its idempotency key.

- [ ] **Step 2: Implement deterministic fictional seed data**

Seed at least 5 branches, 40 parties, mixed dual roles, 30 opportunities across all states, 80 work orders across all states, 250 work events, 4 demo users, and audit history. Use fixed GUIDs and a fixed UTC epoch so screenshots and E2E selectors remain stable.

- [ ] **Step 3: Implement one exclusive reset transaction**

Required order:

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
await db.Database.ExecuteSqlRawAsync(
    "SELECT pg_advisory_xact_lock(4601101)", cancellationToken);
await DeleteDemoOwnedRowsAsync(cancellationToken);
await SeedDeterministicDataAsync(cancellationToken);
await db.SaveChangesAsync(cancellationToken);
await transaction.CommitAsync(cancellationToken);
```

Identity role definitions remain; demo user records are restored deterministically. The service never calls `EnsureDeleted`, drops a schema, or recreates the database.

Persist a `DemoResetExecution` row keyed by a unique idempotency key of at most 64 characters. Its state is `Running`, `Completed`, or `Failed`; a repeated completed key returns the stored completion result, while a concurrent running key waits for the owning transaction and then reads its final state. Add this table in a forward-only `AddDemoResetExecution` migration and prove the unique constraint in an integration test.

On success, the completion audit entry is part of the reset transaction. On failure, first roll back the reset transaction, then use a short separate transaction to persist a sanitized `ResetFailed` audit entry; if PostgreSQL itself is unavailable, the structured error log remains the fallback evidence. This avoids claiming that an audit row inside a rolled-back transaction survived.

- [ ] **Step 4: Implement confirmation and full-page loading state**

On final submit, disable the button, set `aria-busy=true`, reveal a full-page overlay with progress text `初期化しています…`, prevent double submission, and allow navigation only after the server response. On HTTP failure, hide the overlay, restore the button, and show the correlation ID plus retry guidance.

- [ ] **Step 5: Pass reset and concurrency tests**

```powershell
dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~DemoReset"
```

Expected: authorization, rollback, deterministic counts, idempotency, and lock-interleaving tests pass.

- [ ] **Step 6: Commit reset**

```powershell
git add src/FieldOps.Infrastructure/Demo src/FieldOps.Features/Administration src/FieldOps.Web/Controllers/AdministrationController.cs src/FieldOps.Web/Views/Administration src/FieldOps.Web/Views/Shared/_Layout.cshtml src/FieldOps.Web/wwwroot/js/demo-reset.js tests/FieldOps.IntegrationTests/Administration
git commit -m "Add safe administrator demo reset"
```

---

### Task 13: Close security, failure, and concurrency gaps

**Files:**
- Create: `src/FieldOps.Web/Middleware/SecurityHeadersMiddleware.cs`
- Create: `src/FieldOps.Web/Services/RateLimitPolicies.cs`
- Create: `tests/FieldOps.IntegrationTests/Security/SecurityRegressionTests.cs`
- Create: `tests/FieldOps.IntegrationTests/Concurrency/ConcurrentMutationTests.cs`
- Create: `tests/FieldOps.IntegrationTests/Failures/FailurePathTests.cs`
- Modify: `src/FieldOps.Web/Program.cs`

- [ ] **Step 1: Write adversarial regression tests**

Cover CSRF, IDOR by changing IDs, cross-branch query parameters, over-posted BranchId/UserId/Version, open redirect, invalid correlation ID, unsupported method, oversized search term, repeated demo-login posts, repeated reset posts, stale update, deleted-resource update, database timeout, and unhandled exception redaction.

- [ ] **Step 2: Configure security headers and bounded rate limits**

Set Content-Security-Policy for self-hosted assets, frame-ancestors none, X-Content-Type-Options nosniff, Referrer-Policy same-origin, and Permissions-Policy denying camera, microphone, and geolocation. Limit demo login to 20 attempts/minute/IP and reset to 3 attempts/10 minutes/user; return 429 with `Retry-After`.

- [ ] **Step 3: Add concurrent write tests**

Run 20 parallel updates against the same versioned opportunity and assert exactly one succeeds, 19 return a conflict, the final row is valid, and 20 outcomes are logged without an unhandled exception.

- [ ] **Step 4: Run the complete integration suite**

```powershell
dotnet test tests/FieldOps.IntegrationTests --configuration Release
```

Expected: every integration test passes with no skipped security or concurrency test.

- [ ] **Step 5: Commit hardening**

```powershell
git add src/FieldOps.Web tests/FieldOps.IntegrationTests
git commit -m "Harden FieldOps security and concurrency"
```

---

### Task 14: Add Playwright E2E coverage for all four roles

**Files:**
- Create: `tests/FieldOps.E2ETests/Infrastructure/FieldOpsWebFixture.cs`
- Create: `tests/FieldOps.E2ETests/Pages/DemoLoginPage.cs`
- Create: `tests/FieldOps.E2ETests/Pages/DashboardPage.cs`
- Create: `tests/FieldOps.E2ETests/Pages/PartyPage.cs`
- Create: `tests/FieldOps.E2ETests/Pages/SalesPage.cs`
- Create: `tests/FieldOps.E2ETests/Pages/WorkOrderPage.cs`
- Create: `tests/FieldOps.E2ETests/Pages/ResetPage.cs`
- Create: `tests/FieldOps.E2ETests/Roles/SystemAdministratorTests.cs`
- Create: `tests/FieldOps.E2ETests/Roles/BranchManagerTests.cs`
- Create: `tests/FieldOps.E2ETests/Roles/SalesRepresentativeTests.cs`
- Create: `tests/FieldOps.E2ETests/Roles/FieldTechnicianTests.cs`
- Create: `tests/FieldOps.E2ETests/Accessibility/AccessibilitySmokeTests.cs`

- [ ] **Step 1: Install Playwright Chromium reproducibly**

```powershell
dotnet build tests/FieldOps.E2ETests
pwsh tests/FieldOps.E2ETests/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium
```

Expected: the pinned Playwright driver installs Chromium successfully.

- [ ] **Step 2: Write one failing smoke journey per role**

Journeys:

```text
System Administrator: login -> dashboard -> party edit -> audit -> reset -> restored counts
Branch Manager: login -> own-branch dashboard -> party edit -> work scheduling -> denied reset
Sales Representative: login -> customer search -> opportunity create -> allowed transitions -> denied audit
Field Technician: login -> assigned work -> add event -> complete work -> denied party edit
```

Each suite also attempts one direct unauthorized URL and asserts 403. Use `data-testid` only where role/name semantics cannot provide a stable accessible locator.

- [ ] **Step 3: Add screenshots and traces only on failure**

Configure video off, screenshot on failure, trace retain-on-failure, and a per-test isolated browser context. Save outputs under `TestResults/playwright/`, which remains untracked except for selected evidence copied to `docs/evidence/`.

- [ ] **Step 4: Add loading-overlay and duplicate-submit E2E checks**

The administrator test clicks final reset once, immediately attempts a second click, verifies the button is disabled and overlay is visible, waits for completion, and confirms deterministic dashboard counts.

- [ ] **Step 5: Run E2E in Chromium**

```powershell
dotnet test tests/FieldOps.E2ETests --configuration Release -- Playwright.BrowserName=chromium
```

Expected: all four role suites and accessibility smoke checks pass with zero retries.

- [ ] **Step 6: Commit E2E coverage**

```powershell
git add tests/FieldOps.E2ETests
git commit -m "Cover four demo roles with Playwright"
```

---

### Task 15: Add reproducible baseline and stress load tests

**Files:**
- Create: `tests/load/baseline.js`
- Create: `tests/load/stress.js`
- Create: `tests/load/lib/auth.js`
- Create: `tests/load/lib/scenarios.js`
- Create: `tests/load/README.md`
- Create: `scripts/run-load-tests.ps1`
- Create: `scripts/summarize-load-results.ps1`
- Create: `docs/evidence/load-test-template.md`

- [ ] **Step 1: Define deterministic traffic mixes and thresholds**

Baseline: 20 virtual users for 10 minutes. Stress: 100 virtual users for 5 minutes. Both use 70% reads, 20% writes, and 10% dashboard requests. Thresholds:

```javascript
export const thresholds = {
  http_req_failed: ['rate==0'],
  checks: ['rate==1'],
  'http_req_duration{profile:baseline}': ['p(95)<=1000'],
  'http_req_duration{profile:stress}': ['p(95)<=2000'],
};
```

Writes use isolated seeded records per virtual user so expected optimistic conflicts are tested separately, not counted as system errors.

- [ ] **Step 2: Add a preflight and postflight integrity check**

Preflight verifies `/health/ready`, test-data generation, role login, and zero active reset. Postflight queries counts and foreign-key consistency through a test-only command available only in Development and LoadTest environments; it is not mapped in Production.

- [ ] **Step 3: Execute the baseline locally**

```powershell
./scripts/run-load-tests.ps1 -Profile baseline -K6Version 2.2.0
```

Expected: 20 users for 10 minutes, p95 ≤ 1,000 ms, zero failed checks, zero HTTP 500, zero unhandled exceptions, and a passing integrity check.

- [ ] **Step 4: Execute stress locally**

```powershell
./scripts/run-load-tests.ps1 -Profile stress -K6Version 2.2.0
```

Expected: 100 users for 5 minutes, p95 ≤ 2,000 ms, zero failed checks, zero HTTP 500, zero unhandled exceptions, and a passing integrity check.

- [ ] **Step 5: Store sanitized evidence**

Generate `docs/evidence/load-test-results.md` with run timestamp, git SHA, container image IDs, database row counts, request totals, p50/p95/p99, failed checks, HTTP status distribution, exception count, reset count, and integrity result. Exclude host secrets and cookies.

- [ ] **Step 6: Commit load tooling and evidence**

```powershell
git add tests/load scripts docs/evidence/load-test-template.md docs/evidence/load-test-results.md
git commit -m "Add reproducible FieldOps load tests"
```

---

### Task 16: Containerize the application and enforce CI gates

**Files:**
- Create: `Dockerfile`
- Create: `.dockerignore`
- Create: `compose.yaml`
- Create: `.github/workflows/ci.yml`
- Create: `.github/workflows/release.yml`
- Create: `scripts/wait-for-ready.ps1`
- Modify: `src/FieldOps.Web/Program.cs`

- [ ] **Step 1: Write a failing container smoke test**

The script builds the image, starts PostgreSQL and Web, waits up to 120 seconds for `/health/ready`, fetches the login page, checks the title `FieldOps Portal`, and stops the stack while preserving logs as test output.

Run before the Dockerfile exists:

```powershell
docker compose up --build --wait
```

Expected initial result: FAIL because the container definitions do not exist.

- [ ] **Step 2: Create a non-root multi-stage image**

Use `mcr.microsoft.com/dotnet/sdk:10.0` for build and `mcr.microsoft.com/dotnet/aspnet:10.0` for runtime. Publish Release, copy only published output, run as the image-provided non-root user, listen on port 8080, and add an HTTP health check.

- [ ] **Step 3: Create the controlled Compose environment**

Use `postgres:17-alpine`, a named volume, health checks, an internal network, `ASPNETCORE_ENVIRONMENT=Development`, and a local-only connection string. Do not embed a cloud connection string.

- [ ] **Step 4: Add CI in dependency order**

`ci.yml` on pull request and push to main:

```text
restore -> format check -> Release build -> Domain tests -> Integration tests
-> container smoke -> Playwright Chromium -> upload failure artifacts
```

Run integration and E2E with service containers or Testcontainers on GitHub-hosted runners. No job may use `continue-on-error` for a declared quality gate.

- [ ] **Step 5: Add gated release workflow**

`release.yml` triggers only after `ci.yml` succeeds on main and only when the repository variables `KOYEB_APP_NAME` and `KOYEB_SERVICE_NAME` plus secret `KOYEB_TOKEN` exist. It calls Koyeb service redeploy with wait enabled, then verifies `/health/live`, `/health/ready`, login page, and one read-only demo role journey. It does not run database-reset or load-test operations against the public service.

- [ ] **Step 6: Pass the local container and full CI-equivalent checks**

```powershell
dotnet format FieldOps.sln --verify-no-changes
dotnet build FieldOps.sln --configuration Release
dotnet test FieldOps.sln --configuration Release --no-build
docker compose up --build --wait
Invoke-WebRequest http://localhost:8080/health/ready -UseBasicParsing
docker compose down
```

Expected: format, build, tests, container health, and HTTP readiness pass.

- [ ] **Step 7: Commit delivery automation**

```powershell
git add Dockerfile .dockerignore compose.yaml .github scripts src/FieldOps.Web/Program.cs
git commit -m "Containerize and gate FieldOps delivery"
```

---

### Task 17: Write public portfolio and operations documentation

**Files:**
- Create: `README.md`
- Create: `docs/architecture.md`
- Create: `docs/operations.md`
- Create: `docs/testing.md`
- Create: `docs/evidence/verification-summary.md`
- Create: `docs/evidence/screenshots/*.png`
- Create: `scripts/check-readme.ps1`
- Modify: `CONTEXT.md`

- [ ] **Step 1: Write README acceptance tests as a checklist script**

Create a read-only script that fails unless README contains:

- fictional reconstruction disclosure;
- live demo link field supplied from repository metadata after publish;
- four roles and one-click login behavior;
- system screenshots;
- architecture and domain overview;
- local start commands;
- reset safety explanation;
- declared test matrix and latest evidence;
- free-host limitations;
- Japanese primary explanation and concise English summary;
- MIT license only if the user approves that license immediately before publication.

- [ ] **Step 2: Document architecture and operational recovery**

`docs/operations.md` includes migration procedure, health interpretation, reset diagnosis, advisory-lock inspection, correlation-ID log search, Neon restore/branch recovery, Koyeb rollback to a known Git SHA, and local Docker recovery.

- [ ] **Step 3: Document honest quality claims**

`docs/testing.md` separates unit, integration, E2E, accessibility smoke, container smoke, baseline load, stress load, and public-demo smoke. State exact environment and date for every measured result.

- [ ] **Step 4: Capture verified screenshots**

Use Playwright at 1440×900 for dashboard, party details, work-history search, branch progress, mobile navigation, and reset loading state. Store only approved images under `docs/evidence/screenshots/`.

- [ ] **Step 5: Run document checks and commit**

```powershell
./scripts/check-readme.ps1
rg -n "real employer|production source|bug.free|zero bugs" README.md docs
git diff --check
```

Expected: checklist passes; any risky claim is either absent or explicitly negated by the fictional reconstruction disclosure.

```powershell
git add README.md CONTEXT.md docs scripts/check-readme.ps1
git commit -m "Document FieldOps portfolio evidence"
```

---

### Task 18: Run role-owned subagent QA and defect handback

**Files:**
- Create: `docs/evidence/role-qa-matrix.md`
- Create: `docs/evidence/defect-register.md`
- Modify as defects require: only the owning feature files and their regression tests

- [ ] **Step 1: Establish the immutable QA matrix**

Record each role, allowed journeys, denied journeys, browser, viewport, database seed SHA, application git SHA, expected result, actual result, evidence path, and defect ID. The matrix is complete only when every declared cell has an outcome.

- [ ] **Step 2: Assign independent role-verification subagents**

Use four bounded E2E verification lanes:

```text
Role lane 1: System Administrator
Role lane 2: Branch Manager
Role lane 3: Sales Representative
Role lane 4: Field Technician
```

Each verifier runs the automated suite and performs an exploratory pass limited to that role. Verifiers report reproduction steps, correlation ID, expected/actual behavior, screenshot/trace path, severity, and likely owning feature; they do not patch the product.

- [ ] **Step 3: Hand confirmed defects to an implementation subagent**

For each confirmed defect, assign a bounded implementation task with ownership of the minimum affected files. Require a failing regression test first, the smallest correction, targeted test output, and no unrelated refactor. Agents are reminded that they share the codebase and must not revert other edits.

- [ ] **Step 4: Return the fix to the original reporter**

The reporter reruns the exact reproduction and the entire role suite. A defect closes only after the reporter records PASS against the fixing git SHA. Reopened defects return to Step 3 with the new evidence.

- [ ] **Step 5: Run independent final verification**

A verifier who did not implement fixes runs:

```powershell
dotnet format FieldOps.sln --verify-no-changes
dotnet build FieldOps.sln --configuration Release
dotnet test FieldOps.sln --configuration Release --no-build
docker compose up --build --wait
dotnet test tests/FieldOps.E2ETests --configuration Release -- Playwright.BrowserName=chromium
./scripts/run-load-tests.ps1 -Profile baseline -K6Version 2.2.0
./scripts/run-load-tests.ps1 -Profile stress -K6Version 2.2.0
docker compose down
```

Expected: every declared gate passes, no declared test is skipped, and `defect-register.md` contains zero Open or Reopened entries.

- [ ] **Step 6: Commit final QA evidence**

```powershell
git add docs/evidence
git commit -m "Verify all FieldOps roles and quality gates"
```

---

### Task 19: Publish GitHub source and deploy the interactive demo

**Files:**
- Modify: `README.md`
- Modify: `docs/evidence/verification-summary.md`
- Modify: repository settings and external GitHub, Neon, and Koyeb resources only after action-time confirmation

- [ ] **Step 1: Prove the local release candidate before external action**

```powershell
git status --short
git log -1 --oneline
dotnet format FieldOps.sln --verify-no-changes
dotnet build FieldOps.sln --configuration Release
dotnet test FieldOps.sln --configuration Release --no-build
docker compose up --build --wait
Invoke-WebRequest http://localhost:8080/health/ready -UseBasicParsing
docker compose down
```

Expected: clean worktree, all gates pass, and readiness returns HTTP 200.

- [ ] **Step 2: Show exact external targets and request one action-time confirmation**

Present:

```text
GitHub owner/repository: watawatan1984/FieldOps-Portfolio
Visibility: public
Default branch: main
Neon project/database: fieldops-portfolio / fieldops
Koyeb app/service: fieldops-portfolio / fieldops-portal
Koyeb source: confirmed GitHub repository, main branch, Dockerfile builder
Recovery: Git tag plus Koyeb redeploy of prior SHA; Neon restore/branch recovery
Secrets: DATABASE_URL and KOYEB_TOKEN only in provider secret stores
```

If any target differs from the user's GitHub account or provider availability, stop this task and revise the exact target before publication.

- [ ] **Step 3: Create and push the public GitHub repository**

After confirmation, create the repository without generated README/license/gitignore, add it as `origin`, push main, and confirm the remote tree, default branch, Actions status, and public README rendering.

- [ ] **Step 4: Provision Neon and apply migrations safely**

Create the database, store the pooled connection string as `DATABASE_URL`, run the idempotent migration from the release image, then verify migration history and deterministic seed counts. Do not run a destructive schema command.

- [ ] **Step 5: Create the Koyeb app and service**

Use the confirmed GitHub main branch and Dockerfile builder with automatic deployment disabled until CI is green. Configure port 8080, `/health/live`, `/health/ready`, scale-to-zero, and the Neon secret. Enable the gated release workflow after the initial health verification.

- [ ] **Step 6: Verify the public artifact in a real browser**

Check:

- public URL loads after cold start;
- all four one-click role logins work;
- each role sees the correct navigation;
- System Administrator reset shows loading state and restores data;
- a non-admin reset request is denied;
- live and readiness endpoints return 200;
- logs show correlation IDs and no secret values;
- GitHub Actions is green for the deployed SHA.

Do not run baseline or stress load tests against this public free instance.

- [ ] **Step 7: Publish final evidence and tag the release**

Update README with the verified live URL and deployed SHA. Record public smoke results in `docs/evidence/verification-summary.md`, commit, wait for CI and deployment, recheck the final URL, then create annotated tag `v1.0.0` and push that tag.

Expected final state: public source, live demo, green CI, healthy deployment, four working roles, safe reset, and zero known open defects in the declared matrix.

---

## Final Verification Checklist

- [ ] `dotnet format FieldOps.sln --verify-no-changes` passes.
- [ ] Release build passes with zero warnings.
- [ ] Domain, integration, and E2E suites pass with no skipped declared tests.
- [ ] Container smoke and both health endpoints pass.
- [ ] Baseline load: 20 users/10 minutes, p95 ≤ 1 second, zero failed checks, zero HTTP 500, zero unhandled exceptions, data integrity PASS.
- [ ] Stress load: 100 users/5 minutes, p95 ≤ 2 seconds, zero failed checks, zero HTTP 500, zero unhandled exceptions, data integrity PASS.
- [ ] Four-role QA matrix is complete and defect register has zero Open/Reopened entries.
- [ ] Reset is admin-only, confirmed, idempotent, loading-visible, transactionally isolated, rollback-safe, and audited.
- [ ] Logs are structured and sensitive-data redaction tests pass.
- [ ] README clearly identifies the system as a fictional portfolio reconstruction.
- [ ] Public GitHub and Koyeb URLs resolve to the verified release SHA.
