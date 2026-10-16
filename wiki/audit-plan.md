# EF Core Audit Trail Framework
## Complete Functional Requirements, Technical Architecture & Implementation Plan

**Version:** 2.0 — Consolidated Specification  
**Platform:** .NET 10, Entity Framework Core, SQL Server  
**Architectural style:** Clean Architecture  
**Scope:** APIs, background services, Azure Functions and supported bulk database operations  
**Primary keys:** Application-generated GUIDs  
**Audit storage:** Dedicated `AuditTrail` table

---

# 1. Executive Summary

The objective is to implement a centralized, reliable, secure and reusable audit trail framework that records changes to designated business entities without requiring individual repositories or application services to implement auditing logic.

The solution consists of three complementary mechanisms:

**1. Automatic Entity Auditing**

Uses EF Core's `ChangeTracker` and an overridden `SaveChangesAsync` method to capture changes to entities implementing `IAuditableEntity`.

**2. Audited Bulk Operations**

Provides dedicated bulk update and delete operations that capture database changes made outside EF Core's regular change tracker.

**3. Manual Business Auditing**

Allows application services to explicitly record meaningful business activities, including approvals, rejections, processing events and administrative operations.

All three mechanisms must use the same audit schema, value serialization rules, actor context, security rules and history retrieval APIs.

## 1.1 Primary design principles

- Auditing is opt-in at the entity level.
- All entity primary keys are application-generated GUIDs.
- Inserts and hard deletes produce one JSON snapshot per entity.
- Updates produce one record per genuinely changed eligible property.
- Soft deletes are treated as ordinary updates.
- Audit records are immutable through normal application functionality.
- Mandatory audit and business changes must commit atomically.
- Sensitive values are excluded or masked.
- Background processing is supported without an HTTP request context.
- Normal EF Core auditing requires no explicit calls from repositories.
- Unsupported operations must not silently bypass required auditing.
- Audit processing must remain cost-effective and performant.

# 2. Final Audit Behavior

| Operation | Number of records | OldValue | NewValue |
|---|---|---|---|
| Insert | 1 per entity | NULL | Entity JSON |
| Update | 1 per changed field | Previous value | Current value |
| Hard Delete | 1 per entity | Entity JSON | NULL |
| Soft Delete | 1 per changed field | Previous value | Current value |
| Restore Soft Delete | 1 per changed field | Previous value | Current value |
| Bulk Update | 1 per changed field per affected entity | Previous value | Current value |
| Bulk Delete | 1 per entity | Entity JSON | NULL |
| Manual Event | Normally 1 per event | Optional | Optional |

## 2.1 Important terminology

**Entity snapshot:** A JSON document containing all eligible persisted scalar properties of the entity, including its primary key and scalar foreign keys.

**Field-level change:** An audit entry representing the effective modification of one mapped property.

**OperationId:** A GUID grouping audit entries belonging to one logical save or bulk command.

**CorrelationId:** An identifier linking activity across a distributed request or workflow.

**AuditRecordType:** Indicates whether an entry was automatically captured or explicitly created.

**TriggeredBy:** The type of actor responsible for initiating the action.

---

# 3. Database Design

## 3.1 AuditTrail table

| Column | SQL Server type | Purpose |
|---|---|---|
| Id | uniqueidentifier | Primary key |
| PkId | uniqueidentifier NULL | Affected entity ID |
| EntityName | nvarchar(256) | Entity identifier |
| ColumnName | nvarchar(256) NULL | Changed property |
| OldValue | nvarchar(max) NULL | Previous value or JSON |
| NewValue | nvarchar(max) NULL | Current value or JSON |
| DataType | nvarchar(128) NULL | Value type or json |
| Timestamp | datetimeoffset(7) | UTC timestamp |
| TriggeredBy | nvarchar(100) | User, System or Integration |
| UserId | nvarchar(256) NULL | Authenticated user identifier |
| AuditRecordType | nvarchar(20) | Auto or Manual |
| OperationType | nvarchar(30) | Insert, Update, Delete or Custom |
| OperationId | uniqueidentifier | Logical save/operation identifier |
| CorrelationId | nvarchar(100) NULL | Distributed tracing correlation |
| Source | nvarchar(100) NULL | Originating application component |
| EventName | nvarchar(150) NULL | Business event identifier |
| Reason | nvarchar(1000) NULL | Reason for modification |
| Description | nvarchar(max) NULL | Human-readable event description |
| TenantId | nvarchar(100) NULL | Tenant identifier |
| Metadata | nvarchar(max) NULL | Structured JSON context |
| AuditSchemaVersion | smallint | Audit serialization schema version |

## 3.2 Primary keys

All business entities have single-column GUID primary keys generated before persistence.

The audit table must also use application-generated GUIDs.

`PkId` should be nullable to support standalone manual events not associated with a specific entity.

For automatic audit records, `PkId` must always be populated and must never equal `Guid.Empty`.

## 3.3 Column conventions

For JSON snapshots:

- `ColumnName = NULL`
- `DataType = json`
- Populate exactly one of `OldValue` or `NewValue`, depending on the operation.

For field-level updates:

- `ColumnName` identifies the changed property.
- `DataType` identifies its CLR or serialized type.
- `OldValue` and `NewValue` contain its serialized values.

Actual null values must be represented consistently. SQL NULL, JSON null, an empty string and the literal text `"null"` must not be conflated.

`EntityName` must be stable and unambiguous. Prefer the mapped entity type's full name or a centrally configured identifier.

## 3.4 Indexing

Recommended indexes:

1. `(EntityName, PkId, Timestamp DESC)`
2. `(UserId, Timestamp DESC)`
3. `(OperationId)`
4. `(CorrelationId)`
5. `(Timestamp DESC)`

For multi-tenant systems, consider indexes beginning with `TenantId` when queries are tenant-scoped.

Review index costs as audit volume increases. Avoid unnecessary indexes on large text columns.

## 3.5 Database integrity

- Use database constraints for controlled operation types where appropriate.
- Apply UTC consistently.
- Validate JSON metadata where supported.
- Do not create required foreign keys from `AuditTrail.PkId` to business tables.
- Keep deleted entity history available even after physical deletion.
- Restrict direct Update/Delete permissions on audit history.
- Define retention and archival independently of normal CRUD operations.

---

# 4. Domain Contracts

## FR-01: Auditable entity interface

Create the marker interface:

```csharp
public interface IAuditableEntity
{
}
```

Only entities implementing this interface are eligible for automatic auditing.

The implementation must use this contract rather than automatically auditing every tracked entity.

## FR-02: Optional base entity

Provide:

```csharp
public abstract class AuditableEntity
    : IAuditableEntity
{
}
```

Both interface implementation and class inheritance must be supported.

Example:

```csharp
public class FinancialTransaction : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public decimal Amount { get; set; }

    public string Category { get; set; } = "";

    public string Status { get; set; } = "";

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}
```

Do not force all domain entities into a common base-class hierarchy merely to enable auditing.

## FR-03: Property exclusion and masking

Introduce attributes:

```csharp
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditIgnoreAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditMaskAttribute : Attribute
{
}
```

Example:

```csharp
public class Customer : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    [AuditMask]
    public string? Email { get; set; }

    [AuditIgnore]
    public string? AccessToken { get; set; }
}
```

Behavior:

- `AuditIgnore` excludes the property entirely.
- `AuditMask` records that a property changed without revealing its protected value.
- Unannotated eligible properties use the default audit policy.

The framework must also support centralized configuration for globally excluded, masked or unsupported properties.

Sensitive attributes must be applied consistently to Insert snapshots, Update records, Delete snapshots and bulk operations.

---

# 5. Automatic Auditing with SaveChangesAsync

## FR-04: Central persistence integration

Implement the automatic audit mechanism inside a shared `AuditableDbContext` or the application's existing DbContext.

Override:

```csharp
SaveChangesAsync(
    bool acceptAllChangesOnSuccess,
    CancellationToken cancellationToken)
```

Ensure the other asynchronous overload routes through the same auditing mechanism without executing it twice.

The project must also decide whether synchronous `SaveChanges` is supported with equivalent auditing or prohibited explicitly.

## 5.1 Processing workflow

```text
Application changes entity
          |
          v
SaveChangesAsync
          |
          v
ChangeTracker.DetectChanges()
          |
          v
Collect Added / Modified / Deleted
          |
          v
Filter IAuditableEntity
          |
          v
Resolve user and operation context
          |
          v
Generate audit records
          |
          v
Add audit entries to same DbContext
          |
          v
base.SaveChangesAsync()
          |
          v
SQL Server transaction commits
business changes + audit history
```

## FR-05: Insert auditing

For each newly inserted auditable entity:

- Generate exactly one audit record.
- Capture a complete JSON snapshot of eligible properties.
- Store the snapshot in `NewValue`.
- Set `OldValue = NULL`.
- Set `ColumnName = NULL`.
- Set `DataType = json`.
- Set `OperationType = Insert`.
- Set `AuditRecordType = Auto`.

The entity GUID must already be assigned.

The snapshot must use EF Core property metadata and not blindly serialize the entity's navigation graph.

### Insert example

New entity:

```json
{
  "Id": "550e8400-e29b-41d4-a716-446655440000",
  "Amount": 1250.75,
  "Category": "Uncategorized",
  "Status": "Pending",
  "IsDeleted": false
}
```

Generated audit entry:

| Field | Value |
|---|---|
| OperationType | Insert |
| ColumnName | NULL |
| OldValue | NULL |
| NewValue | JSON snapshot |
| DataType | json |
| AuditRecordType | Auto |

Only one record is generated, regardless of the number of included properties.

### Insert limitations

Some fields may be generated or modified by SQL Server during insertion, such as computed columns, database defaults and row-version values.

The initial framework must either exclude those values from pre-save snapshots or implement an approved mechanism for capturing their persisted values.

Do not claim that a pre-save snapshot necessarily reflects database-generated values.

## FR-06: Update auditing

For Modified entities:

1. Read EF Core's original and current property values.
2. Identify properties marked as modified.
3. Determine whether their values genuinely differ.
4. Apply exclusions and masking.
5. Create one audit record for each eligible changed property.

### Update example

Previous entity state:

```json
{
  "Category": "Uncategorized",
  "Status": "Pending"
}
```

Current state:

```json
{
  "Category": "Travel",
  "Status": "Approved"
}
```

Generated records:

| ColumnName | OldValue | NewValue |
|---|---|---|
| Category | Uncategorized | Travel |
| Status | Pending | Approved |

Both records share:

- EntityName
- PkId
- OperationId
- CorrelationId
- Timestamp and actor context

No full-entity snapshot is generated for a normal Update.

### Update comparison requirements

- Ignore unchanged properties.
- Do not assume every property marked Modified has a genuinely different value.
- Use consistent comparison and serialization policies.
- Preserve decimal precision and date/time semantics.
- Support nullable values, enums and GUIDs.
- Account for configured EF Core value comparers.
- Do not accidentally record changes to navigation properties.

## FR-07: Hard Delete auditing

For an entity in `EntityState.Deleted` that is physically removed:

- Generate exactly one audit record.
- Capture all eligible persisted scalar values before deletion.
- Store the JSON snapshot in `OldValue`.
- Set `NewValue = NULL`.
- Set `ColumnName = NULL`.
- Set `DataType = json`.
- Set `OperationType = Delete`.
- Set `AuditRecordType = Auto`.

### Hard Delete example

OldValue:

```json
{
  "Id": "550e8400-e29b-41d4-a716-446655440000",
  "Amount": 1250.75,
  "Category": "Travel",
  "Status": "Approved",
  "IsDeleted": false
}
```

One audit record is inserted.

The snapshot must be created from reliably known property values. For disconnected entities, a deletion request containing only the primary key is insufficient for a complete historical snapshot.

## FR-08: Soft Delete auditing

Soft deletion is a regular Update, not a physical Delete.

Example:

```csharp
transaction.IsDeleted = true;
transaction.DeletedAt = DateTimeOffset.UtcNow;
```

Generated audit records:

| ColumnName | OldValue | NewValue |
|---|---|---|
| IsDeleted | false | true |
| DeletedAt | NULL | UTC timestamp |

Do not generate a full JSON snapshot for soft deletion.

Restoring a deleted record, such as changing `IsDeleted` to false, must also generate normal field-level Update records.

If application infrastructure automatically converts a Deleted state into a soft-delete update, this conversion must occur before audit records are constructed.

## FR-09: Non-auditable and unchanged entities

- Entities not implementing `IAuditableEntity` must be ignored.
- Unchanged entities must produce no audit records.
- Updates without any eligible effective property changes must produce no automatic records.
- The `AuditTrail` persistence model must never be audited recursively.

## FR-10: Related entities and navigation properties

For the initial implementation:

- Include ordinary mapped scalar properties.
- Include scalar foreign keys.
- Exclude navigation object graphs.
- Explicitly define behavior for shadow properties.
- Define whether owned and complex types are supported.
- Exclude or specially handle large binary payloads.

Database cascades that execute without corresponding tracked entity changes must not be assumed to be audited.

---

# 6. Change Detection and Value Serialization

## FR-11: AuditValueSerializer

Introduce a centralized serializer responsible for converting supported property values into reliable audit representations.

The serializer must handle:

- string
- bool
- Guid
- short/int/long
- float/double/decimal
- enum
- DateTime
- DateTimeOffset
- nullable values
- configured JSON and complex types

Requirements:

1. Use culture-independent representations.
2. Preserve meaningful numeric precision.
3. Handle UTC and timezone offsets consistently.
4. Preserve null values.
5. Apply sensitive-property policies before persistence.
6. Produce deterministic JSON property ordering where practical.
7. Avoid unintended serialization of navigation graphs.
8. Include a snapshot schema version for compatibility.

## FR-12: Original value accuracy

EF Core normally retains original values for entities loaded through tracking queries.

The framework must prefer:

```text
Load tracked entity
Modify properties
SaveChangesAsync
```

For detached entities attached later using `Update`, genuine original database values might not be available.

The implementation must adopt one of these explicit policies:

- Load the current entity from the database and apply changes.
- Supply and validate trusted original values.
- Reject unsupported detached update/delete scenarios.

Never invent historical OldValue data.

Where concurrency accuracy matters, use an appropriate concurrency token or another approved consistency strategy.

---

# 7. Audit Context and Identity

## FR-13: IAuditContextProvider

Create a context abstraction that supplies metadata about the operation being performed.

Example:

```csharp
public sealed record AuditContextInfo(
    string TriggeredBy,
    string? UserId,
    string Source,
    string? CorrelationId,
    string? TenantId,
    string? Reason
);

public interface IAuditContextProvider
{
    AuditContextInfo GetCurrent();
}
```

## 7.1 Required execution contexts

| Scenario | TriggeredBy | Source |
|---|---|---|
| Authenticated API request | User | WebAPI |
| Azure Function processing | System | AzureFunction |
| Scheduled job | System | Scheduler |
| External integration | Integration | IntegrationAPI |
| Queue consumer | System | BackgroundWorker |

Background processing must not require `HttpContext`.

The identity abstraction must receive reliable context from authentication middleware, message-processing scope or explicit trusted service identity.

Never trust arbitrary client-supplied user identifiers as authoritative audit identities.

## FR-14: Operation and correlation identifiers

`OperationId` must be generated once per logical save operation or audited bulk command.

All field-level records generated by that operation share the same identifier.

`CorrelationId` should represent the broader request or distributed workflow and be propagated across service boundaries where possible.

Example:

```text
API request
 CorrelationId = ABC-123
      |
      +-- SaveChanges
      |   OperationId = GUID-A
      |
      +-- Service Bus message
              |
              +-- Azure Function
                  CorrelationId = ABC-123
                  OperationId = GUID-B
```

The two operations are individually identifiable but remain associated with the same workflow.

## FR-15: Timestamp conventions

- Use UTC for all recorded timestamps.
- Prefer DateTimeOffset for audit persistence.
- Capture timestamps through a centralized time provider.
- Keep timestamps consistent across all audit entries produced by one logical operation where appropriate.
- Use audit timestamps and IDs for stable history ordering, with a deterministic tie-breaker.

---

# 8. Manual Business Audit Events

## FR-16: Manual audit writer

Implement an injectable audit service for operations that cannot be expressed sufficiently through ordinary entity changes.

Suggested interface:

```csharp
public interface IAuditTrailWriter
{
    void AddManual(AuditEvent auditEvent);

    Task WriteStandaloneAsync(
        AuditEvent auditEvent,
        CancellationToken cancellationToken = default);
}
```

Suggested `AuditEvent` data:

- EntityName
- PkId
- EventName
- Description
- Reason
- Optional ColumnName
- Optional OldValue
- Optional NewValue
- Metadata
- OperationId
- CorrelationId
- Actor context

Manual events must set `AuditRecordType = Manual`.

## 8.1 Examples

- FinancialTransactionApproved
- DocumentRejected
- TaxPackUploaded
- CategorizationRequested
- CategorizationRetried
- PredictionOverridden
- ReportExported
- ExternalDataSynchronized
- AdministrativeActionPerformed

### Manual event example

```json
{
  "EventName": "TransactionApproved",
  "EntityName": "FinancialTransaction",
  "PkId": "550e8400-e29b-41d4-a716-446655440000",
  "Reason": "Verified by finance reviewer",
  "Description": "Transaction approved after review",
  "Metadata": {
    "ApprovalLevel": 2,
    "ApprovalMethod": "ManualReview"
  }
}
```

## FR-17: Transactional manual auditing

For a manual event associated with business modifications:

- Stage the manual record in the current DbContext.
- Save it together with business changes.
- Ensure failure of mandatory audit persistence rolls back the business update.
- Reuse the logical operation context where applicable.

## FR-18: Standalone manual auditing

For independent activities such as report exports or user actions that do not modify business entities:

- Allow explicit audit persistence without requiring a business-entity change.
- Ensure the event is saved deliberately.
- Prevent recursive automatic auditing of the audit record itself.
- Define error-handling expectations for events whose primary action occurs outside the database.

Do not imply that SQL transaction atomicity extends to external file downloads, HTTP calls or message delivery.

---

# 9. Audited Bulk Operations

## FR-19: Why separate handling is necessary

`ExecuteUpdateAsync` and `ExecuteDeleteAsync` bypass EF Core's normal tracked-entity persistence flow.

Therefore, the `SaveChangesAsync` override cannot automatically produce accurate audit records for these methods.

The framework must include a dedicated audited bulk-operations abstraction.

## 9.1 Recommended interface

Use an abstraction resembling:

```csharp
public interface IAuditedBulkOperations
{
    Task<int> UpdateAsync<T>(
        AuditedBulkUpdateSpec<T> specification,
        CancellationToken cancellationToken = default)
        where T : class, IAuditableEntity;

    Task<int> DeleteAsync<T>(
        AuditedBulkDeleteSpec<T> specification,
        CancellationToken cancellationToken = default)
        where T : class, IAuditableEntity;
}
```

The specification types must define supported filters, allowed update properties and value expressions.

**Important:** Do not assume arbitrary EF Core LINQ predicates and SetProperty expressions can automatically be transformed into SQL Server OUTPUT-based audited commands by a generic wrapper.

The first implementation should support a deliberately restricted and validated set of bulk operations. More general support can be added later.

## FR-20: Bulk Update behavior

A bulk update must:

1. Receive a validated bulk update specification.
2. Identify the actual rows modified.
3. Capture original and updated values using a reliable database operation.
4. Generate one audit entry per genuinely changed eligible property per affected entity.
5. Apply masking, exclusion and serialization policies.
6. Assign one OperationId to the logical bulk command.
7. Persist audit records in the same transaction.
8. Return the number of affected business rows.

### Bulk Update example

Business operation:

```csharp
await context.FinancialTransactions
    .Where(x => x.Status == "Pending")
    .ExecuteUpdateAsync(setters => setters
        .SetProperty(x => x.Category, "Travel"));
```

In the audited design, this direct call is replaced by an approved bulk operation.

SQL Server can capture changed values using:

```sql
DECLARE @Changed TABLE
(
    Id UNIQUEIDENTIFIER,
    OldCategory NVARCHAR(MAX),
    NewCategory NVARCHAR(MAX)
);

UPDATE t
SET Category = @Category
OUTPUT
    DELETED.Id,
    DELETED.Category,
    INSERTED.Category
INTO @Changed
FROM dbo.FinancialTransactions AS t
WHERE t.Status = N'Pending';

SELECT Id, OldCategory, NewCategory
FROM @Changed;
```

This is an illustrative fragment to be executed within the application's coordinated transaction. The category is parameterized, and production code must apply exact comparison and audit policies.

After successfully executing the update, the application constructs field-level audit records from the returned actual changes, generates their GUIDs in C#, and persists them within the same transaction.

For large batches, use efficient set-based audit insertion such as a table-valued parameter or an approved bulk-copy strategy.

Do not commit until audit persistence succeeds.

## FR-21: Bulk Delete behavior

Bulk Delete must:

1. Execute an approved database deletion command.
2. Capture the actual deleted rows using `OUTPUT DELETED`.
3. Produce a JSON snapshot of eligible columns for every affected entity.
4. Generate exactly one audit record per deleted entity.
5. Store JSON in `OldValue`.
6. Set `NewValue = NULL`.
7. Set `ColumnName = NULL`.
8. Save the generated records in the same transaction.
9. Return the number of deleted business rows.

The audit serializer must apply the same exclusions and masking rules as ordinary deletes.

## FR-22: Bulk soft deletion

Bulk soft deletion must be treated as Bulk Update.

For example, changing `IsDeleted` and `DeletedAt` must produce property-level audit records.

No full entity snapshot is required.

## FR-23: Bulk transaction rules

Bulk operations must use explicit transaction coordination because the actual data modification and audit insertion are separate persistence steps.

Requirements:

- Start or participate in an appropriate database transaction.
- Capture the actual rows affected by SQL.
- Construct and persist audit records.
- Commit only after both parts succeed.
- Roll back the operation if either part fails.
- Integrate correctly with the configured EF Core execution strategy.
- Support cancellation and exception propagation.
- Define how existing caller-managed transactions are handled.

Do not use unprotected before-and-after queries as the default approach, because concurrent changes may make the resulting audit records inaccurate.

## FR-24: Bulk operation enforcement

Direct use of `ExecuteUpdateAsync` and `ExecuteDeleteAsync` on auditable entities must not silently bypass the required framework.

Recommended controls:

- Approved bulk operations abstraction.
- Architectural tests or code analysis.
- Restricted repository access where practical.
- Code review rules.
- Explicit exceptions for operations where auditing is intentionally not required.

Unsupported bulk operations should fail clearly or use an approved tracked-entity fallback.

## FR-25: Tracked entity consistency

Bulk operations do not synchronize entities already tracked in the DbContext.

The implementation must avoid mixing audited bulk modifications with stale tracked objects.

Possible policies:

- Use a dedicated short-lived DbContext for bulk operations.
- Refresh affected tracked entities when necessary.
- Prohibit unsafe combinations within the same unit of work.

The preferred initial policy is a dedicated bulk-operation execution scope.

---

# 10. Alternative: Database Triggers

SQL Server triggers are a valid alternative when changes must be audited regardless of which application performs them.

Triggers can use the `inserted` and `deleted` virtual tables to inspect affected rows.

Advantages:

- Covers ordinary EF Core operations.
- Covers ExecuteUpdate and ExecuteDelete.
- Covers raw SQL and external database writers.
- Runs within SQL Server transaction processing.

Disadvantages:

- Auditing logic moves partly into the database.
- C# marker interfaces and attributes are not automatically available.
- Sensitive-property configuration may be duplicated.
- Capturing authenticated application identity requires deliberate context propagation.
- A trigger-based mechanism combined with the SaveChanges auditor may create duplicate records.
- Schema and trigger maintenance become coupled.

### Decision

Use **SaveChangesAsync + AuditedBulkOperations** for the initial framework.

Consider triggers later only if comprehensive database-level coverage becomes a business or compliance requirement.

If triggers are adopted, define a single authoritative writer for each audited table and operation.

---

# 11. Transactions, Reliability and Idempotency

## FR-26: Atomic normal saves

Automatic audit records must be added to the same DbContext before persistence.

Call the underlying `base.SaveChangesAsync` only once for a normal logical save.

For SQL Server, use EF Core's normal transactional persistence behavior.

The operation must not succeed if mandatory audit records fail to persist.

## FR-27: Audit failure handling

If SaveChangesAsync fails:

- Corresponding database audit records must roll back.
- The exception must propagate to the caller.
- In-memory audit entries must be handled safely.
- A subsequent retry must not accidentally duplicate previously staged records.
- Failed commands must not be represented as successful committed business changes.

Use ordinary structured application logging to record failures.

## FR-28: Idempotency

The framework must account for retries caused by:

- Transient SQL Server connection failures.
- EF Core execution strategies.
- Background-message redelivery.
- HTTP command retries.
- Application-level retry policies.

Requirements:

- Audit IDs must be application-generated.
- Logical operation identifiers must remain stable across retries of the same command when appropriate.
- Duplicate staged audit entries must be prevented.
- Existing business idempotency keys must be reused where available.
- Recovery from uncertain transaction commits must be designed and tested.

Application-generated GUIDs reduce some retry risks but do not by themselves make every update idempotent.

## FR-29: EF Core SaveChanges semantics

Special care is required for:

- `acceptAllChangesOnSuccess = false`
- Existing tracked audit entries after failure
- Execution strategy retries
- Repeated calls using the same DbContext
- SaveChanges return values
- Concurrency exceptions

The implementation must explicitly define whether the SaveChanges return count includes audit entries. The simplest policy is to preserve the underlying EF Core behavior and document the resulting count rather than silently changing the API contract.

## FR-30: Concurrency control

For normally tracked updates, preserve EF Core optimistic concurrency behavior.

If a concurrency conflict prevents a business update, the corresponding audit records must not commit.

Bulk operations must implement concurrency conditions explicitly where required, since they do not automatically use tracked concurrency tokens.

## FR-31: Operation consistency

An OperationId groups one logical save or bulk execution.

If the same business command spans several saves or distributed service calls, use CorrelationId and optional higher-level business event identifiers to associate them.

Do not imply that one SQL transaction spans multiple services or independent databases.

---

# 12. Security and Audit Integrity

## FR-32: Sensitive information

Apply consistent policies to:

- Passwords
- API keys
- Access tokens
- Connection strings
- Authentication secrets
- Personal identifiers
- Financial or regulated personal data where applicable

Support Include, Mask and Ignore.

Never capture arbitrary object graphs or request bodies merely because they are available during persistence.

## FR-33: Immutable audit history

Audit records must be append-only through normal application workflows.

Requirements:

- No ordinary Update/Delete API for audit entries.
- No automatic re-auditing of audit records.
- Restricted database modification permissions.
- Separation between audit writing and history reading permissions where practical.
- Controlled administrative retention and archival processes.
- Explicit handling of legally required data deletion or anonymization.

For high-assurance compliance systems, consider tamper-evident storage or cryptographic verification in a later phase.

## FR-34: Tenant isolation

If the system is multi-tenant:

- Persist trusted tenant context.
- Enforce tenant-aware audit retrieval authorization.
- Prevent cross-tenant history disclosure.
- Validate tenant relationships on manual entries.
- Apply equivalent isolation to bulk operations.

## FR-35: Data minimization

Audit only information required for accountability, investigation and business requirements.

A full JSON snapshot means all **eligible persisted fields**, not every CLR property, navigation object, calculated field or secret.

---

# 13. Audit History Retrieval

## FR-36: Query service

Provide an abstraction such as:

```csharp
public interface IAuditTrailQueryService
{
    Task<PagedResult<AuditTrailDto>> SearchAsync(
        AuditTrailQuery query,
        CancellationToken cancellationToken = default);
}
```

Support filtering by:

- EntityName
- PkId
- UserId
- OperationType
- AuditRecordType
- EventName
- OperationId
- CorrelationId
- TenantId
- Timestamp range

## FR-37: Presentation grouping

The query service should support grouping multiple field-level changes under one logical operation.

Example:

```json
{
  "entityName": "FinancialTransaction",
  "operationType": "Update",
  "userId": "U123",
  "changes": [
    {
      "columnName": "Category",
      "oldValue": "Uncategorized",
      "newValue": "Travel"
    },
    {
      "columnName": "Status",
      "oldValue": "Pending",
      "newValue": "Approved"
    }
  ]
}
```

For Insert and Delete, the response should expose the corresponding entity snapshot.

Use read-only queries, appropriate authorization and pagination.

## FR-38: Additional query capabilities

The design should support future requirements such as:

- Entity history timeline.
- User activity history.
- Investigation by correlation identifier.
- Before-and-after comparison.
- Manual business event history.
- Audit exports with authorization.
- Administrative review.

---

# 14. Performance and Scalability

## FR-39: Minimize unnecessary work

- Filter non-auditable entities immediately.
- Avoid recording unchanged properties.
- Avoid querying old values separately for normally tracked entities.
- Build automatic audit records in memory.
- Avoid unnecessary serialization of excluded properties.
- Avoid recursive SaveChanges calls.
- Avoid serializing large navigation graphs.
- Use efficient SQL Server batching.

## FR-40: Large-volume Insert behavior

The single-snapshot Insert design reduces audit-row count significantly.

Example:

Inserting 10,000 entities with 12 eligible properties:

| Strategy | Audit records |
|---|---:|
| One audit record per property | 120,000 |
| One JSON snapshot per entity | 10,000 |

This reduces record count, but actual storage savings depend on JSON size, indexing and SQL Server storage characteristics.

## FR-41: Bulk processing performance

For large operations:

- Use set-based SQL.
- Capture changes through OUTPUT where supported.
- Avoid loading thousands of entities into the change tracker unnecessarily.
- Use configurable batch sizes if required.
- Evaluate table-valued parameters or approved bulk insertion methods for audit records.
- Keep database transactions short.
- Measure locking and transaction-log pressure.
- Track audit-row creation volume.

## FR-42: Retention and growth

Define:

- Retention periods.
- Archival requirements.
- Purging permissions.
- Storage growth monitoring.
- Index maintenance.
- Possible partitioning for very large history tables.

Retention must not silently remove history required by regulation or business policy.

---

# 15. Observability and Diagnostics

## FR-43: Application logging integration

The audit framework should integrate with the application's existing structured logging and tracing infrastructure.

Use Serilog, OpenTelemetry or Application Insights as appropriate to report:

- Audit generation failures.
- Audit persistence failures.
- Audit record counts.
- Bulk operation durations.
- Slow history queries.
- Retry activity.
- Unexpected unsupported write paths.

Do not log raw sensitive OldValue/NewValue payloads into application telemetry.

## FR-44: Separation of concerns

Maintain the following distinction:

**Audit Trail:** What business entity or business action changed, who initiated it and when.

**Structured Logging:** What application operations occurred and whether errors were encountered.

**Distributed Tracing:** How requests traveled through APIs, functions, queues and downstream services.

**Metrics:** Audit counts, latency, failures and storage growth.

Audit history should not replace operational observability.

---

# 16. Proposed Clean Architecture Structure

```text
src/
  Domain/
    Common/
      IAuditableEntity.cs
      AuditableEntity.cs
      AuditIgnoreAttribute.cs
      AuditMaskAttribute.cs

  Application/
    Abstractions/
      Auditing/
        IAuditContextProvider.cs
        IAuditTrailWriter.cs
        IAuditTrailQueryService.cs
        IAuditedBulkOperations.cs

    Models/
      Auditing/
        AuditContextInfo.cs
        AuditEvent.cs
        AuditTrailQuery.cs
        AuditedBulkUpdateSpec.cs
        AuditedBulkDeleteSpec.cs

  Infrastructure/
    Persistence/
      ApplicationDbContext.cs
      Configurations/
        AuditTrailConfiguration.cs
      Migrations/

    Auditing/
      AuditTrail.cs
      AuditTrailBuilder.cs
      AuditValueSerializer.cs
      AuditPropertyPolicy.cs
      AuditTrailWriter.cs
      AuditTrailQueryService.cs
      SqlServerAuditedBulkOperations.cs
      HttpAuditContextProvider.cs
      BackgroundAuditContextProvider.cs

  API/
    Controllers/
      AuditHistoryController.cs

tests/
  UnitTests/
    Auditing/

  IntegrationTests/
    Auditing/

  ArchitectureTests/
    Auditing/

  PerformanceTests/
    Auditing/
```

### Layer responsibilities

**Domain**

Defines which entities are eligible for auditing and optional property policies.

**Application**

Defines interfaces for audit context, manual events, bulk commands and audit queries.

**Infrastructure**

Implements EF Core change tracking, serialization, SQL Server bulk auditing and persistence.

**API / Workers**

Supply the execution context and use the abstractions without directly generating automatic audit entries.

The audit persistence model belongs in Infrastructure unless auditing itself becomes a core domain aggregate.

---

# 17. Implementation Workflow

## 17.1 Automatic audit pseudocode

```csharp
SaveChangesAsync()
{
    // 1. Apply configured pre-save transformations
    //    such as soft-delete conversion.

    // 2. Detect changes.

    // 3. Find Added / Modified / Deleted
    //    entries implementing IAuditableEntity.

    // 4. Resolve AuditContext.

    // 5. Generate or reuse OperationId.

    // 6. Create audit records:
    //    Added    -> single NewValue JSON
    //    Modified -> field-level changes
    //    Deleted  -> single OldValue JSON

    // 7. Stage generated audit records.

    // 8. Stage any pending manual events.

    // 9. Execute base.SaveChangesAsync once.

    // 10. Handle successful state acceptance,
    //     retries, exceptions and cleanup.
}
```

This is design pseudocode, not a drop-in implementation. Production code must account for all SaveChanges overloads and state-management edge cases.

## 17.2 Bulk audit pseudocode

```text
Validate bulk specification
       |
Resolve actor context
       |
Create/reuse OperationId
       |
Start coordinated transaction
       |
Execute parameterized UPDATE/DELETE
with SQL Server OUTPUT
       |
Receive actual affected row values
       |
Build audit records in C#
       |
Generate audit GUIDs
       |
Persist audit records
       |
Commit transaction
       |
Return affected business row count
```

The SQL UPDATE/DELETE and audit persistence must run on the same coordinated transactional resource.

---

# 18. Implementation Phases

## Phase 1 — Foundation and Database Schema

**Tasks**

- Finalize AuditTrail schema.
- Create the AuditTrail EF Core entity.
- Configure all fields and indexes.
- Add SQL Server migration.
- Implement IAuditableEntity.
- Implement optional AuditableEntity base.
- Implement AuditIgnore and AuditMask.
- Define audit enums/constants.
- Define snapshot versioning.

**Deliverables**

- Database migration.
- Domain auditing contract.
- Configuration definitions.

**Exit criteria**

AuditTrail table is created, mapped and tested.

## Phase 2 — Automatic Auditing Core

**Tasks**

- Implement AuditTrailBuilder.
- Implement AuditValueSerializer.
- Implement AuditPropertyPolicy.
- Capture Insert JSON snapshots.
- Capture field-level Updates.
- Capture Hard Delete JSON snapshots.
- Treat Soft Delete as Update.
- Integrate with SaveChangesAsync.
- Prevent recursive auditing.
- Validate application-generated GUID usage.

**Deliverables**

Working automatic auditing for tracked entities.

**Exit criteria**

Insert, Update, Hard Delete and Soft Delete pass integration tests, with correct field values and record counts.

## Phase 3 — Context and Actor Identification

**Tasks**

- Implement IAuditContextProvider.
- Add HTTP request identity resolution.
- Add background-service context.
- Add Azure Function context.
- Add tenant and correlation handling.
- Introduce consistent UTC timestamp generation.
- Ensure actor identity is trusted.

**Deliverables**

Accurate actor and source metadata.

**Exit criteria**

Audit entries correctly identify both user-triggered and system-triggered operations.

## Phase 4 — Manual Audit Support

**Tasks**

- Implement IAuditTrailWriter.
- Support transactional staging.
- Support standalone audit persistence.
- Define event metadata serialization.
- Add business event examples.
- Ensure manual entries are excluded from automatic auditing.

**Deliverables**

Manual business audit APIs.

**Exit criteria**

Manual events persist correctly, including events associated with ordinary business updates.

## Phase 5 — Audited Bulk Operations

**Tasks**

- Define supported bulk update and delete specifications.
- Implement SQL Server OUTPUT-based change capture.
- Implement bulk property-level Update auditing.
- Implement bulk Delete JSON snapshots.
- Support bulk soft deletion.
- Implement coordinated transactions.
- Generate audit IDs in C#.
- Implement efficient audit insertion for large batches.
- Enforce approved bulk-operation usage.
- Document unsupported expressions.

**Deliverables**

Production-capable audited bulk operations.

**Exit criteria**

Bulk operations capture accurate changes without relying on stale tracked entity values, and both business and audit writes roll back together.

## Phase 6 — Reliability and Security Hardening

**Tasks**

- Handle concurrency exceptions.
- Implement retry-safe operation state.
- Handle SaveChanges failures and cleanup.
- Define `acceptAllChangesOnSuccess` behavior.
- Handle detached-entity limitations.
- Implement sensitive data policies.
- Restrict audit history modification.
- Enforce tenant isolation.
- Test high-volume rollback.
- Review transaction commit uncertainty.
- Document exception and recovery behavior.

**Deliverables**

Hardened, secure and reliable framework.

**Exit criteria**

Critical failure and retry scenarios pass SQL Server integration tests.

## Phase 7 — Querying and Reporting

**Tasks**

- Implement audit history query service.
- Add entity-history retrieval.
- Add filters and pagination.
- Add grouped history DTOs.
- Add secured history API.
- Add correlation-based activity lookup.
- Optimize database indexes.

**Deliverables**

Read-only audit history APIs.

**Exit criteria**

Authorized users can retrieve complete history efficiently, with correct Insert/Delete snapshots and Update comparisons.

## Phase 8 — Performance, Documentation and Release

**Tasks**

- Benchmark normal and audited SaveChanges.
- Benchmark bulk changes.
- Measure SQL transaction overhead.
- Evaluate audit storage growth.
- Write developer integration guidelines.
- Document bulk-operation restrictions.
- Document retention and monitoring policy.
- Add architecture tests.
- Validate migrations and deployment procedures.
- Complete final security review.

**Deliverables**

Production deployment package, developer documentation and performance baseline.

**Exit criteria**

All mandatory acceptance criteria pass, with no unexplained audit gaps in supported operations.

---

# 19. Acceptance Criteria

| ID | Test case | Expected result |
|---|---|---|
| AC-01 | Insert auditable entity | Exactly one JSON snapshot |
| AC-02 | Insert non-auditable entity | Zero audit records |
| AC-03 | Update two properties | Exactly two audit records |
| AC-04 | Update without actual changes | Zero audit records |
| AC-05 | Hard delete entity | Exactly one OldValue JSON snapshot |
| AC-06 | Soft delete entity | Field-level Update records |
| AC-07 | Restore soft-deleted entity | Field-level Update records |
| AC-08 | Ignore annotated property | Property excluded |
| AC-09 | Mask sensitive property | Sensitive value protected |
| AC-10 | Save entity with GUID PK | Correct PkId captured |
| AC-11 | Modify multiple entities in one save | Shared OperationId |
| AC-12 | Save from authenticated API | Correct user context |
| AC-13 | Save from Azure Function | Correct system identity |
| AC-14 | Save manual business event | Manual entry created |
| AC-15 | Business save fails | No committed corresponding audit |
| AC-16 | Audit persistence fails | Business transaction rolls back |
| AC-17 | Concurrency conflict | No false success history |
| AC-18 | Transient retry | No accidental duplicate history |
| AC-19 | Detached update without originals | Explicit supported handling |
| AC-20 | Bulk Update | Correct old/new field values |
| AC-21 | Bulk Delete | One JSON snapshot per deleted entity |
| AC-22 | Bulk soft delete | Standard field-level Update |
| AC-23 | Failed bulk audit insertion | Bulk business change rolls back |
| AC-24 | Unsupported bulk command | Explicit rejection or approved fallback |
| AC-25 | Query by entity and GUID | Correct chronological history |
| AC-26 | Query by CorrelationId | Related operations retrieved |
| AC-27 | Cross-tenant audit query | Unauthorized records inaccessible |
| AC-28 | Sensitive data in Insert/Delete JSON | Proper masking/exclusion |
| AC-29 | Repeated save without state acceptance | No accidental duplicate records |
| AC-30 | Large batch processing | Meets agreed performance requirements |

---

# 20. Testing Strategy

## 20.1 Unit tests

Test individual components:

- Value comparison.
- JSON serialization.
- Nullable values.
- Decimal precision.
- Date/time representation.
- Enum and GUID handling.
- Property masking/exclusion.
- Audit record creation.
- Operation grouping.
- Non-auditable entity filtering.

## 20.2 Integration tests

Use a real SQL Server test database for relational behavior.

Test:

- Multiple entity writes in one save.
- Transaction rollback.
- Concurrency conflicts.
- Foreign-key failures.
- Generated GUID primary keys.
- Manual events combined with automatic changes.
- Bulk SQL OUTPUT capture.
- Bulk operation rollback.
- SaveChanges state behavior.
- Transient retry scenarios.

Avoid relying exclusively on EF Core's in-memory provider for transaction and concurrency tests.

## 20.3 Performance tests

Measure:

- Single-entity Insert/Update/Delete overhead.
- Large tracked SaveChanges operations.
- Large audited bulk updates.
- Large audited bulk deletes.
- Audit record storage size.
- Query latency.
- SQL Server lock contention.
- Transaction-log growth.

Compare behavior with and without auditing to establish overhead.

No universal latency threshold should be assumed. Establish acceptable values from representative application workloads.

## 20.4 Architecture tests

Verify:

- Auditable entities implement the expected marker contract.
- AuditTrail is excluded from automatic auditing.
- Prohibited direct bulk-operation APIs are not used in audited repositories.
- Infrastructure does not leak into Domain.
- Expected interfaces are used by application services.
- Supported save paths cannot silently bypass mandatory auditing.

---

# 21. Key Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Missing original values | Prefer tracked entities or approved original-value strategy |
| Sensitive data leakage | Central masking and exclusion policies |
| Duplicate records on retry | Stable operation IDs, safe retry state and idempotency |
| Bulk auditing bypass | Enforced audited bulk abstraction |
| Excessive audit volume | JSON snapshots for Insert/Delete, selective field Updates |
| Transaction failures | Atomic persistence and rollback testing |
| Large JSON payloads | Field eligibility and size policies |
| Stale tracked entities after bulk update | Dedicated bulk execution scope |
| Cross-tenant information exposure | Tenant-scoped authorization |
| Schema evolution | AuditSchemaVersion and stable serialization |
| Database-generated values missing in snapshot | Exclude or explicitly capture persisted values |
| Recursive audit generation | Exclude AuditTrail and use safe DbContext flow |
| SQL-side changes outside approved paths | Explicit coverage limitation or future database auditing |

---

# 22. Future Enhancements

The initial framework should avoid unnecessary complexity, but remain extensible for:

1. Configurable per-entity auditing strategies.
2. Entity-level Update snapshots for selected scenarios.
3. Audit dashboards and advanced reporting.
4. Archived history in cheaper storage.
5. Tamper-evident or cryptographically chained records.
6. SQL Server trigger-based coverage of external database writes.
7. Temporal-table integration for selected entities.
8. Audit event streaming to a central analytics platform.
9. Outbox-based publication of committed audit events.
10. Compliance-specific retention and access policies.
11. Schema-driven auditing for additional databases.
12. Advanced field-level diffing for approved JSON properties.

These are future capabilities and should not complicate the first implementation unless required by business needs.

---

# 23. Developer Usage Examples

## Example A — Automatic Insert

```csharp
var transaction = new FinancialTransaction
{
    Id = Guid.NewGuid(),
    Amount = 1500,
    Category = "Travel",
    Status = "Pending"
};

context.FinancialTransactions.Add(transaction);

await context.SaveChangesAsync();
```

**Expected:** One automatic audit record containing the inserted entity's eligible JSON snapshot in NewValue.

## Example B — Automatic Update

```csharp
var transaction =
    await context.FinancialTransactions
        .SingleAsync(x => x.Id == transactionId);

transaction.Category = "Meals";
transaction.Status = "Approved";

await context.SaveChangesAsync();
```

**Expected:** Up to two field-level records, provided both values genuinely changed.

## Example C — Soft Delete

```csharp
transaction.IsDeleted = true;
transaction.DeletedAt = DateTimeOffset.UtcNow;

await context.SaveChangesAsync();
```

**Expected:** Field-level Update records. No complete entity snapshot.

## Example D — Hard Delete

```csharp
context.FinancialTransactions.Remove(transaction);

await context.SaveChangesAsync();
```

**Expected:** One automatic audit record containing the deleted entity's eligible JSON snapshot in OldValue.

## Example E — Manual Event

```csharp
auditTrailWriter.AddManual(
    new AuditEvent
    {
        EntityName = "FinancialTransaction",
        PkId = transactionId,
        EventName = "TransactionApproved",
        Description = "Approved after manual review"
    });

await context.SaveChangesAsync();
```

**Expected:** Manual business event saved along with any pending business modifications. AuditEvent is an illustrative application DTO.

## Example F — Audited Bulk Update

```csharp
await auditedBulkOperations.UpdateAsync(
    new AuditedBulkUpdateSpec<FinancialTransaction>
    {
        // Approved filter and update specification
        // defined by the implementation.
    },
    cancellationToken);
```

**Expected:** Every affected entity has the correct property-level history based on actual SQL Server changes.

The concrete bulk specification API must restrict supported filters and updates rather than pretending to support arbitrary LINQ expressions without a translation strategy.

---

# 24. Final Architecture Decisions

The initial production implementation will use:

| Area | Final decision |
|---|---|
| Main auditing method | SaveChangesAsync override |
| Eligible entities | IAuditableEntity |
| Entity identifiers | Application-generated GUID |
| Audit row identifiers | Application-generated GUID |
| Insert recording | Single NewValue JSON snapshot |
| Update recording | One record per changed property |
| Hard Delete recording | Single OldValue JSON snapshot |
| Soft Delete recording | Standard Update |
| Bulk Update/Delete | Dedicated SQL Server-aware service |
| Bulk data capture | OUTPUT INSERTED / DELETED |
| Manual business events | IAuditTrailWriter |
| Auditing storage | Same SQL Server database |
| Normal transaction | Shared EF Core SaveChanges transaction |
| Bulk transaction | Explicit coordinated transaction |
| Actor identification | IAuditContextProvider |
| Correlation | OperationId + CorrelationId |
| Sensitive fields | Mask or Ignore |
| Reporting | Secured query service |
| Audit mutability | Append-only |
| Database triggers | Not included in initial version |

# 25. Definition of Done

The framework is production-ready when:

- All opted-in tracked entities are audited automatically.
- Inserts and hard deletes produce accurate single-record snapshots.
- Normal and soft-delete updates produce accurate field-level history.
- Manual events can be recorded with trusted context.
- Supported bulk operations produce complete and accurate audit history.
- Business and mandatory audit persistence are atomic.
- GUIDs, timestamps, actor identity and correlation are correct.
- Sensitive data is protected in both property records and JSON snapshots.
- Save failures, retries, concurrent updates and disconnected entities are handled according to documented rules.
- History retrieval is efficient, secure and tenant-aware where applicable.
- Tests cover normal operations, failures and supported high-volume scenarios.
- Developers have clear conventions preventing unintended audit bypasses.

**Guiding architectural principle:** Implement auditing once, enforce it at the persistence boundary, preserve transaction integrity, and make exceptions explicit. The framework should provide reliable accountability without forcing repetitive audit logic into individual business services or introducing unnecessary distributed components.