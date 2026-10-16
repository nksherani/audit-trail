# Audit Trail Demo

.NET 10 / EF Core 10 / SQL Server audit framework implementing
[the audit plan](wiki/audit-plan.md) with Clean Architecture.

## Structure

| Project | Responsibility |
| --- | --- |
| `src/AuditTrail.Domain` | Business entities, auditing marker and property attributes |
| `src/AuditTrail.Application` | CRUD use cases and audit/bulk/query abstractions |
| `src/AuditTrail.Infrastructure` | EF Core, migrations, automatic/manual audits, SQL Server bulk capture |
| `src/AuditTrail.Api` | HTTP endpoints, trusted actor resolution, authorization, composition |
| `tests/AuditTrail.Tests` | Unit, real SQL Server, HTTP, architecture, resilience and performance tests |

Dependencies point inward. Domain and Application have no EF Core or ASP.NET
references. [Implementation decisions and coverage](wiki/audit-implementation.md)
explain supported paths, security, retries, tenancy and deployment.

## Run locally

Requires .NET SDK 10.0.300 or a later 10.0.3xx patch and SQL Server. The default
connection uses `localhost`, Windows authentication, database `AuditTrailDemo`.
Change `ConnectionStrings:DefaultConnection` in the API's appsettings.json or set
`ConnectionStrings__DefaultConnection` in the environment. Keep credentials out
of source control. TrustServerCertificate is intended for local development.

```powershell
dotnet restore
dotnet tool restore
dotnet ef database update --project src/AuditTrail.Infrastructure --startup-project src/AuditTrail.Api
dotnet run --project src/AuditTrail.Api --launch-profile http
```

API: `http://localhost:5063`; development OpenAPI: `/openapi/v1.json`.
Migrations are applied explicitly, never automatically by the web host. The
existing migration IDs and demo data are preserved by the project restructuring.

## Try the demo

`src/AuditTrail.Api/AuditTrail.Api.http` includes CRUD calls for categories,
suppliers and products. Each resource supports list/get/create/update/delete.
IDs and FKs are GUIDs generated in C# before persistence. Product foreign keys
restrict category/supplier deletion while products still reference them.

Automatic auditing needs no repository calls:

- Insert: one eligible JSON snapshot in NewValue.
- Update: one record per genuinely changed eligible field.
- Hard delete: one eligible JSON snapshot in OldValue.
- Product soft delete/restore: ordinary field changes.
- Supplier email is masked; internal notes and generated rowversions are excluded.

`POST /api/products/{id}/soft-delete` and `/restore` demonstrate soft deletion.
Soft-deleted rows remain visible in this demo so restoring/reviewing them is easy.
`ImportCheckpoint` demonstrates an intentionally non-auditable operational entity.

## Protected audit endpoints

Configure `Authentication__Authority` with a trusted HTTPS OpenID Connect issuer
and `Authentication__Audience` with the API audience. Tokens must contain a
validated subject, appropriate role claims, and `tenant_id` when using tenants.
No authentication bypass or development token issuer is built into the API.

| Route | Required role |
| --- | --- |
| `GET /api/audit-history` | AuditReader |
| `GET /api/audit-history/grouped` | AuditReader |
| `POST /api/products/{id}/review` | AuditWriter |
| `POST /api/products/bulk-update` | AuditWriter |
| `POST /api/products/bulk-delete` | AuditWriter |

Examples are in `src/AuditTrail.Api/AuditTrail.Auditing.http`. History supports
entity/GUID, actor, event, record/operation type, operation/correlation ID,
time range and pagination. Tenant claims scope queries and writes; supplying a
TenantId query parameter cannot elevate access.

Existing CRUD routes remain anonymous for the local demo. Those operations are
recorded as System/WebAPI in the null-tenant namespace. Authenticated tenants
cannot see that namespace's business data/history. Secure CRUD before production.

## Manual events and workers

Inject `IAuditTrailWriter`, stage `AddManual(AuditEvent)`, then save the business
unit of work. `WriteStandaloneAsync` uses its own context and does not flush
pending business changes. `BackgroundAuditContext` supports trusted Function,
scheduler, integration and queue scopes without HttpContext. See the integration
examples and failure/replay rules in `wiki/audit-implementation.md`.

Bulk commands use validated explicit GUID IDs, equality filters and constant
assignments. SQL Server OUTPUT captures actual old/new values. Audit writes and
business SQL share one transaction; SqlBulkCopy inserts audit rows in batches.
Direct EF ExecuteUpdate/Delete and raw business SQL are not approved write paths.

## Verify

```powershell
dotnet test AuditTrail.slnx
# With the API running (PowerShell 7):
pwsh -File scripts/Smoke-Test.ps1
```

Tests create/migrate/drop only a uniquely named `AuditTrailTests_<GUID>` database
on local SQL Server. Set `AUDIT_TEST_SQLSERVER` to use another test instance. The
account needs permission to create/drop that test database and create users for
SQL permission checks. There is no EF in-memory transaction substitute.

The performance test covers 1,000 rows and records timings without inventing an
SLO. See [the measured baseline](wiki/performance-baseline.md).

## Database deployment

```powershell
dotnet ef migrations add YourMigration --project src/AuditTrail.Infrastructure --startup-project src/AuditTrail.Api
dotnet ef migrations script --idempotent --project src/AuditTrail.Infrastructure --startup-project src/AuditTrail.Api --output deployment.sql
```

Review/backup before applying migrations to a non-demo database. Apply
`scripts/sql/audit-permissions.sql` with a deployment principal and assign a
non-owner runtime login to the appropriate writer/reader roles. The script denies
ordinary audit UPDATE/DELETE and separates retention permissions. Local Windows
login memberships are not changed automatically. Retention defaults to keeping
history indefinitely; no automatic purge job is installed.
