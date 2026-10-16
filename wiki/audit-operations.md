# Why the AuditOperations table exists

`AuditOperations` stores a transactional receipt for a completed save or audited
bulk command. It supports retry safety and recovery when the application cannot
tell whether SQL Server committed a transaction.

The audit plan requires handling retries and uncertain commits (FR-27 through
FR-29). The receipt table is an implementation decision supporting those
requirements; it is not needed merely to capture changed fields.

## AuditTrail versus AuditOperations

| Table | Question answered | Typical row count |
| --- | --- | --- |
| AuditTrail | What changed, who initiated it, and when? | One snapshot per insert/delete, one row per changed field, or a manual event |
| AuditOperations | Has this logical database command already completed? | One receipt per saved unit of work or bulk command |

`AuditTrail.OperationId` corresponds to `AuditOperations.Id`. For example, a save
that changes a product's Price and StockQuantity produces two audit rows sharing
one operation ID and one operation receipt. No foreign key is required between
these tables in the current mapping.

A receipt can exist without any audit rows: a non-auditable entity might change,
only ignored fields might change, or a bulk command might affect no rows. A
completely empty normal save returns zero without creating a receipt.

## The failure it addresses

Consider an update using operation ID `A`:

1. The application stages the business change, audit records, and receipt `A`.
2. SQL Server commits all of them in the same transaction.
3. The connection fails before the application receives confirmation.
4. The application retries the same logical command with operation ID `A`.
5. The framework finds receipt `A`, validates the command identity, and recognizes
   the previous success rather than applying the write again.

Without a receipt, the application might mistake an acknowledgement failure for
a rollback. Repeating the command could create duplicate history or repeat a
business action. Entity GUIDs alone cannot prove that an update or delete committed.

If the transaction actually rolls back, its receipt rolls back too. The framework
can then attempt the command again. A receipt inside a caller-managed transaction
does not prove final commitment until that caller commits the transaction.

## Stored fields

| Column | Purpose |
| --- | --- |
| Id | Application-assigned logical OperationId and primary key |
| TenantId | Trusted tenant scope associated with the command |
| Kind | `Save`, `BulkUpdate`, or `BulkDelete` |
| Fingerprint | SHA-256 digest of the command representation used to detect incompatible reuse |
| ResultCount | Result returned when a matching completed operation is replayed |
| Timestamp | UTC time recorded for the operation |

The fingerprint is a command-matching digest. It is not an audit payload, a
cryptographic signature, or proof that history has not been tampered with.
Old/new business values remain in AuditTrail subject to its masking and exclusion
policies.

For normal saves, ResultCount includes business entries, audit entries, and the
receipt itself, following the framework's EF SaveChanges count convention. For
bulk commands, it represents affected business rows.

## How the framework uses it

For a normal tracked save, business rows, automatic/manual audit rows, and the
receipt are staged in the same DbContext before one underlying SaveChangesAsync.
On failure, generated audit entries are detached so they are not staged twice.
The pending operation ID remains available for retry. Where commit verification
finds a matching receipt, the framework can recover the successful result and
database-generated rowversions.

For bulk operations, SQL Server OUTPUT captures the actual affected values. The
business command, audit insertion, and receipt insertion share an explicit SQL
transaction. The receipt is written before committing. Replaying a command with
the same operation ID checks its receipt before performing another bulk write.

The tenant, kind, and fingerprint must match. Reusing an operation ID for a
different command is rejected. The receipt's primary key also prevents multiple
receipts for the same operation ID; concurrent duplicate attempts may require a
subsequent retry to observe the winning committed receipt.

## Limits and caller responsibilities

- Reuse an operation ID only for retries of the same logical command. Use a new
  ID for a new save or bulk command.
- When reconstructing a command after redelivery, preserve its original generated
  business IDs and compatible command values. Rebuilding an update from a newer
  database state can change its fingerprint; a conflict is safer than assuming it
  is the same command.
- The table does not make arbitrary HTTP requests automatically idempotent. The
  demo CRUD API does not implement response caching or Idempotency-Key middleware.
- Explicit bulk OperationIds support command replay. The supported UtcNow marker
  keeps bulk soft-delete command identity stable across timestamp retries.
- Cancellation and unrecoverable failures still propagate. A receipt does not
  guarantee that every network failure can be resolved immediately.
- Receipts do not coordinate transactions across databases, services, HTTP calls,
  downloads, or queue acknowledgement.
- SaveChangesAsync(false) still requires explicit state acceptance before another
  save; receipts do not replace ChangeTracker.AcceptAllChanges().

## Retention and permissions

Receipts are append-only through the ordinary application path. The deployment
permission script denies UPDATE/DELETE to the ordinary writer role. A separate
administrative retention role controls any approved archival or deletion.

Retain receipts for at least the supported retry/redelivery window. Removing a
receipt removes the evidence used to recognize that operation on a later replay.
The demo defaults to retaining both history and receipts indefinitely.

## Could the table be removed?

Basic automatic change auditing could work without it. However, removing it would
require replacing the uncertain-commit verification and command-replay mechanism,
or explicitly weakening those guarantees. AuditTrail alone is insufficient as a
universal completion marker because some successful commands produce no audit rows.

See [save/recovery and bulk design](audit-implementation.md) for the broader
implementation. ResilienceTests, AutomaticAuditTests, and BulkAndHistoryTests cover
lost commit acknowledgement, retries, operation replay, and duplicate prevention.
