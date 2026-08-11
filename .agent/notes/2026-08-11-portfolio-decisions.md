---
date: 2026-08-11
context: FieldOps Portal public C# portfolio
sensitivity: L1_shareable
status: approved
---

# Approved decisions

- Build a public, interactive C#/.NET portfolio rather than a documentation-only repository.
- Present the system as a fictional reconstruction for demonstration, not as the source of a real employer's production system.
- Use ASP.NET Core MVC on .NET 10, Entity Framework Core, PostgreSQL, Docker, Koyeb, and Neon.
- Keep recurring cloud cost at zero and accept cold starts in the public demo.
- Use an integrated multi-page system covering parties, customers, business partners, sales opportunities, work orders, work history search, branches, users, audit history, and settings.
- Use an integrated dashboard as the default layout; search and branch-progress views remain separate pages.
- Provide one-click demo login for System Administrator, Branch Manager, Sales Representative, and Field Technician roles.
- Show the button label `初期化`; only System Administrators can execute it.
- Run reset only on demand, with confirmation, loading state, database locking, rollback, and audit evidence.
- Target ordinary operation at 20 concurrent users and execute a separate 100-user stress test outside the free public instance.
- Require role-specific subagent E2E testing, implementation hand-back, reporter retest, and independent final verification.
- Define completion as all declared tests passing with zero known defects in the declared test matrix; do not claim that all possible defects are impossible.

# Ruled out

- A documentation-only portfolio, because it would not substantiate implementation ability.
- A single-page mockup, because it would not demonstrate real workflows.
- Automatic periodic data reset, because the user explicitly requested an administrator-operated reset button.
- A microservice architecture, because it adds deployment and operational complexity without improving this portfolio's evidence.
- Render's expiring free PostgreSQL and minute-scale cold start as the primary hosting path.
- A paid-by-default cloud architecture, because the approved monthly budget is zero.

# Follow-up

- Produce the approved design specification and implementation plan.
- Build, test, publish to GitHub, deploy to Koyeb and Neon, then verify the public artifact and evidence reports.
