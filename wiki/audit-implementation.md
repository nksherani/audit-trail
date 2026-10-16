# Audit plan review and implementation

The consolidated plan in `audit-plan.md` is implemented in four projects. The
original specification is preserved. This document resolves its deliberate
implementation choices and describes the supported persistence boundary.

## Architecture

```text
Domain ← Application ← Infrastructure
             ↑               ↑
             └────── API ────┘  (composition root)
```

Domain contains entities, `IAuditableEntity`, the optional `AuditableEntity`, and
Ignore/Mask/JSON attributes. It references no persistence or web framework.
Application contains CRUD use cases, store abstractions, audit context, events,
bulk specifications, and query contracts. Infrastructure owns EF Core, migrations,
SQL Server OUTPUT, audit models, serialization, and transactions. API supplies
validated HTTP identity and exposes application services. Existing migration IDs
were preserved when moving them into Infrastructure.

Category, Supplier, and Product opt in. ImportCheckpoint deliberately opts out.
All keys are assigned GUIDs, with `ValueGeneratedNever()`. Supplier.Email is
masked and Supplier.InternalNote is ignored to demonstrate both policies.

## Persistence and serialization decisions

- Inserts: one eligible scalar snapshot in NewValue; deletes: one in OldValue.
- Updates (including soft delete/restore): one record per genuinely changed field.
- EF value comparers determine equality before masking. A protected field can
  therefore have equal redacted old/new representations while genuinely changing.
- Navigation graphs, shadow properties, binary fields, computed/default-generated
  values and rowversions are excluded. GUID primary keys and scalar FKs are included.
- Native EF owned/complex graphs are explicitly rejected. A mapped scalar with an
  explicit value converter and `[AuditJson]` can opt into structured JSON; nested
  secret names are redacted. Arbitrary CLR graphs are rejected by default.
- Scalar values are JSON tokens. SQL NULL means an actual null value; `""` is an
  empty string and `"null"` is text. Snapshots use JSON null inside the document.
- JSON property ordering is deterministic. Schema version is 1. Decimal precision,
  enum names, GUIDs and date offsets are preserved using culture-independent JSON.
  Decimal values exceeding mapped scale are rejected for tracked writes so the
  pre-save audit does not describe precision that the database would round away.
- Audit timestamps use TimeProvider and UTC DateTimeOffset. Business timestamp
  values retain their represented offsets; new DateTime properties should use an
  explicit UTC mapping/conversion rather than infer a missing timezone.
- Default maximum serialized value is 65,536 characters. Oversized required audit
  data fails the business write; it is never silently truncated. Configure policies
  centrally via `AddAuditTrailInfrastructure(connection, options => ...)`.

## Save semantics and recovery

`AppDbContext.SaveChangesAsync(bool, CancellationToken)` detects changes, checks
GUIDs and tracked provenance, applies trusted tenant context, builds automatic and
manual rows, and calls the underlying EF save once. The audit row IDs are generated
in C#. Audit records cannot be inserted directly, modified or deleted through the
normal context. Both synchronous save overloads throw explicitly.

The same SQL transaction includes business rows, audit rows and an AuditOperations
receipt. Receipts contain an operation ID, tenant, command fingerprint, timestamp
and result count. No old/new payload is stored in a receipt. SaveChanges returns
EF's affected-entry count, **including audit rows and the receipt**, rather than
pretending it is a business-row count.

SQL Server retries are enabled. Generated audit IDs stay fixed during EF's internal
retry. On failed saves generated rows are detached, manual events remain staged,
the pending operation ID remains stable and the original failure propagates unless
a committed matching receipt verifies success. A replay verifies command kind,
tenant and fingerprint. Database-generated rowversions are recovered on successful
receipt replay. Never reuse an operation ID for a different command.

`SaveChangesAsync(false)` preserves EF state. Another save with pending unaccepted
changes throws before issuing SQL. Call `ChangeTracker.AcceptAllChanges()` after a
successful save before reusing the context. This is an explicit guard against
duplicate inserts/history, not an implicit acceptance of the caller's state.

Category, Supplier and Product use SQL rowversion optimistic concurrency. A failed
business concurrency check rolls back its audits. Tracked query entities and
entities successfully saved by this context have reliable provenance; detached
Update/Delete is rejected. Load a tracked entity and apply requested fields instead.
Detach/reattach does not confer trusted provenance.

For repeatable commands in a worker, assign the **same business IDs and OperationId**
on redelivery. Application-generated IDs alone do not make arbitrary HTTP requests
idempotent. The demo CRUD API does not cache HTTP responses or provide a transparent
Idempotency-Key middleware. A business command must persist/reuse its command ID
and original generated IDs when automatic command replay is needed. Bulk APIs
accept an explicit operation ID. A changed command reusing that ID is rejected.

Caller-managed SQL transactions remain subject to EF execution-strategy rules:
wrap the whole transaction in its execution strategy. An audit receipt in an open
caller transaction proves that save reached that transaction, not that the caller
has committed it. Roll back and dispose a failed unit of work rather than treat
in-memory state acceptance as a database transaction boundary. No SQL transaction
extends to external HTTP requests, downloads or queue acknowledgement.

## Context, tenancy, authorization

The HTTP adapter uses authenticated `sub`/NameIdentifier and `tenant_id` claims,
role claims and the current distributed trace. User/tenant headers are ignored.
Anonymous demo CRUD writes are System/WebAPI operations in the null-tenant namespace.
The smoke-test script removes its business records; their audit history is retained
as expected for an append-only trail.
Existing demo rows remain in that namespace after migration.
Auditing starts with this deployment; it does not invent historical events for
rows that existed before the audit schema was installed.

BackgroundAuditContext has no HttpContext dependency. Set its Current property
inside a trusted DI message scope **before resolving the DbContext**:

```csharp
await using var scope = serviceProvider.CreateAsyncScope();
scope.ServiceProvider.GetRequiredService<BackgroundAuditContext>().Current = new(
    TriggeredBy: "System", Source: "AzureFunction", CorrelationId: message.CorrelationId,
    TenantId: trustedTenant, Reason: "Queue processing", OperationId: commandId);
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
// Load tracked entities, change them, await db.SaveChangesAsync(cancellationToken).
```

Use Source Scheduler, BackgroundWorker or IntegrationAPI and TriggeredBy
Integration as appropriate. Set UserId only from trusted authentication/service
context. A context's tenant cannot change during its unit of work.

Business queries are tenant-filtered. Product references are checked against that
tenant, including bulk FK assignments. Entity-bound manual events must reference
a mapped entity visible in the current tenant (or a same-save inserted/deleted
entity). Standalone events without a PkId are scoped to the trusted tenant.

History requires an authenticated AuditReader role and enforces the trusted tenant
again inside its query service. Supplying a different TenantId never elevates
access. Worker history access defaults to denied unless the host explicitly
registers a trusted IAuditAccessProvider. Review and bulk HTTP endpoints require
AuditWriter. Configure Authentication:Authority and Authentication:Audience for a
real HTTPS issuer; no test authentication handler is included in the application.
Anonymous CRUD is retained for the local demo; secure those routes before using
the API as a production business system.

## Manual events

```csharp
writer.AddManual(new AuditEvent {
    EntityName = typeof(Product).FullName!, PkId = product.Id,
    EventName = "ProductReviewed", Reason = "Inventory check",
    Metadata = new Dictionary<string, object?> { ["ReviewLevel"] = 2 }
});
await db.SaveChangesAsync(cancellationToken); // shared operation and transaction

await writer.WriteStandaloneAsync(new AuditEvent {
    EntityName = "Reports", EventName = "ReportExported",
    Description = "Export completed"
}, cancellationToken); // separate context; never flushes pending business changes
```

Manual events always use trusted actor and tenant context. Optional field old/new
values require a mapped entity/property so normal ignore/mask rules apply. Raw
manual snapshots are rejected; use redacted Metadata for structured event data.
Do not put secrets into free-text descriptions, reasons, or unclassified metadata
keys. The event's optional OperationId must agree with other staged events and
the trusted operation context. Standalone events prohibit ambient transactions
because they deliberately use their own context and transaction.

## Bulk contract

The supported specification is deliberately narrower than LINQ:

- Explicit unique GUID IDs (1–10,000 by default), plus optional equality conditions.
- Constant scalar assignments, at most 20. No expressions, arithmetic, arbitrary
  SQL, navigation paths, key changes, rowversion changes or tenant reassignment.
- Optional 8-byte expected rowversions covering every requested ID (maximum 500
  rows); any missing/stale row rolls the complete operation back.
- Soft delete is a normal constant update of IsDeleted and DeletedAt.
- `BulkValue.UtcNow` is the sole built-in value expression: it is resolved once
  from the audit operation clock into a date/time parameter. Its stable command
  representation lets soft-delete retries reuse an OperationId without timestamp
  drift. No arbitrary expressions are translated.
- Database affected-row counts are returned, even when effective values were equal
  and therefore no field history was necessary.

The service opens a dedicated context and refuses an ambient/caller-managed
transaction or any tracked business object in its calling scope. This prevents
stale tracked values from being reused after bulk writes. Execute the command in
a fresh scope. All identifiers are validated from EF metadata and quoted; values
are parameters. IDs use OPENJSON rather than thousands of SQL parameters.

UPDATE captures DELETED and INSERTED values; DELETE captures DELETED snapshots.
SQL rowversions and equality conditions are part of the actual WHERE clause.
Audit records are generated in C# and inserted with SqlBulkCopy in 1,000-row
batches, with constraints enabled, on the same SQL connection and transaction.
Only then is the operation receipt written and the transaction committed. An EF
execution strategy surrounds the complete unit and verifies receipts on replay.
Capture is bounded in memory by MaxBulkRows; larger jobs must use explicit chunks
with separate operation IDs and a shared correlation ID.

## Queries and operational boundaries

Search supports all plan filters: entity, GUID, user, operation/record type, event,
operation ID, correlation, tenant and time range. Default page size is 50, maximum
200. Ordering is Timestamp then Id. Grouped pagination pages complete filtered
operations rather than splitting field records; large operations can naturally
produce large groups. Snapshots are returned unchanged in OldValue/NewValue.

Direct EF ExecuteUpdate/Delete, raw business SQL, SQL triggers and external writers
are outside the approved write boundary. Application/API have no EF write access;
architecture tests prohibit direct SQL/EF bulk APIs there. Infrastructure business
repositories must use tracked saves or the audited bulk service. Database cascades
without tracked entries are not covered; current business FKs use Restrict.
Do not introduce post-audit SavingChanges callbacks/interceptors that mutate
business values; apply transformations before entering SaveChangesAsync.

Run `scripts/sql/audit-permissions.sql` as a deployment principal. It defines
writer, reader and separate retention roles. Assign a dedicated non-owner runtime
login; db_owner/sysadmin can bypass ordinary permission restrictions. The local
Windows-authenticated development login is not demoted or reassigned by setup.
The demo host needs writer and reader database roles, while HTTP role checks still
separate read access. Keep migrations on a distinct privileged deployment identity.

Retention defaults to **retain indefinitely**. No automatic delete/purge job is
installed. Archive/purge only under an approved business retention schedule using
the separate retention identity. Retain operation receipts at least as long as the
maximum supported command-redelivery window; purging receipts changes the
idempotency guarantee. Legal deletion/anonymization needs a controlled administrative
process and must not be implemented by ordinary entity CRUD.

Meter `AuditTrail.Framework` exposes audit.records, audit.failures,
audit.bulk.duration and audit.query.duration. Structured logging records operation
IDs, counts, failures and queries exceeding one second, without raw values. Connect
the meter/logger to the deployment's monitoring backend. Monitor audit/receipt row
growth, database/log size, write failures, retry frequency, p95 latencies, index
fragmentation and blocking. The one-second warning is diagnostic, not a performance
SLO. Export these measurements through the host's existing OpenTelemetry pipeline
if one is deployed; an external telemetry backend is not bundled with this demo.

## Validation and deployment

Tests run against a fresh `AuditTrailTests_<GUID>` SQL Server database, migrate
from scratch, and delete only that generated database. Set AUDIT_TEST_SQLSERVER
to another SQL Server connection when required. SQL account permissions tests
create roles/users only in that temporary database. Security tests validate both
unauthenticated rejection and authenticated role/tenant isolation in the HTTP host.

Acceptance coverage is in AutomaticAuditTests (AC-01–19, AC-28–29),
BulkAndHistoryTests (AC-14, AC-20–28), ResilienceTests (AC-18 and commit uncertainty),
AuthenticatedApiTests (AC-12, AC-27), SerializationTests, SqlPermissionsTests,
ApiAndArchitectureTests and PerformanceTests (AC-23, AC-30).
The suite checks row counts and values, concurrency, cancellation, failed-write
cleanup, explicit replay, false acceptance, and actual transactional rollback.

Use the migration commands in README. Back up an existing database before schema
deployment. The previous GUID migration intentionally cannot recover original
integer keys on downgrade. AddAuditFramework itself has a generated down migration,
but rolling it back deletes audit data and is an administrative deployment decision.
Create/upgrade a test database first, apply permission roles, configure issuer and
audience, assign runtime users, and validate authenticated access before deployment.
No credentials, issuer keys, production login memberships, or retention schedule
are guessed by this repository.
