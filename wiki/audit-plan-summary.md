# Audit Trail — Short Implementation Explanation

We automate auditing at the **EF Core persistence boundary**. Business services
change entities and call `SaveChangesAsync()` as usual; the DbContext creates the
audit records before saving.

## 1. Opt in with IAuditableEntity; exclude fields with AuditIgnore

```csharp
public interface IAuditableEntity { }

// Abbreviated Supplier example; navigation/tenant/concurrency fields omitted.
public class Supplier : IAuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    [AuditMask]
    public string Email { get; set; } = "";

    [AuditIgnore]
    public string? InternalNote { get; set; }
}
```

- **IAuditableEntity:** Only implementing entities are automatically audited.
  Inheriting the optional `AuditableEntity` base also opts in.
- **AuditIgnore:** The property is completely absent from insert/delete snapshots
  and field-update records. Changing only InternalNote creates no audit row.
- **AuditMask:** Records a genuine change while replacing protected values with
  `[REDACTED]`. This applies to snapshots, updates, and supported bulk operations.

## 2. Automate through SaveChangesAsync and EF change detection

A normal business update needs no explicit auditing call:

```csharp
var supplier = await db.Suppliers.SingleAsync(x => x.Id == id);
supplier.Name = "Beta Supplies";
await db.SaveChangesAsync();
```

The overridden SaveChangesAsync performs these steps:

1. Call `ChangeTracker.DetectChanges()` to identify Added, Modified, and Deleted entries.
2. Keep entries implementing `IAuditableEntity`; exclude the audit persistence models.
3. Read tracked original/current scalar values. For updates, record only properties
   that are marked modified **and genuinely differ**, using EF value comparers.
4. Apply AuditIgnore/AuditMask and build the records: one JSON snapshot for an
   insert/delete, or one row per changed eligible field for an update.
5. Assign one OperationId, UTC timestamp, and trusted actor context to the save;
   stage audit records, any manual events, and an operation receipt.
6. Call `base.SaveChangesAsync(...)` **once**. Business rows, audit records, and
   the receipt commit or roll back in the same SQL Server transaction.

```text
Business changes → DetectChanges → IAuditableEntity filter → Ignore/Mask policies
                 → Build audit records + receipt → One underlying save
```

GUID keys are assigned in C#, so PkId is known before saving. No second save is
needed to retrieve generated keys. Updates/deletes must use reliably tracked
entities; unsupported detached writes are rejected.

## 3. Example rows in AuditTrail

S1/P1 and O1–O6 below are aliases for real GUIDs. Entity names and JSON are
shortened for presentation; actual snapshots contain all eligible mapped scalar
fields, including IDs/FKs, and use full entity names. P1 is an existing Product.
All rows also carry timestamp, actor, tenant, source, and correlation context.

| OperationId | Entity / PkId | Record type | Operation | Column / Event | OldValue | NewValue |
| --- | --- | --- | --- | --- | --- | --- |
| O1 | Supplier / S1 | Auto | Insert | — | NULL | Supplier JSON: Name="Alpha", Email="[REDACTED]" |
| O2 | Supplier / S1 | Auto | Update | Name | `"Alpha"` | `"Beta Supplies"` |
| O2 | Supplier / S1 | Auto | Update | Email | `"[REDACTED]"` | `"[REDACTED]"` |
| O2 | Supplier / S1 | Manual | Custom | SupplierReviewed | NULL | NULL |
| O3 | Product / P1 | Auto | Update | IsDeleted | `false` | `true` |
| O3 | Product / P1 | Auto | Update | DeletedAt | NULL | UTC date/time string |
| O4 | Supplier / S1 | Auto | Delete | — | Supplier JSON: Name="Beta Supplies", Email="[REDACTED]" | NULL |
| O5 | Product / P1 | Auto | Update (bulk) | Price | `100.00` | `120.00` |
| O6 | Product / P1 | Auto | Delete (bulk) | — | Product JSON snapshot | NULL |

InternalNote never appears. The email changed in O2 even though both stored
representations are masked. SupplierReviewed is EventName, not a changed column.
Soft delete is an Update. Bulk rows store OperationType `Update`/`Delete` too;
“(bulk)” above explains the capture path, not a separate stored operation type.

Manual business events are explicitly staged with `IAuditTrailWriter.AddManual()`
and can share the business save, as in O2. Bulk commands use the dedicated audited
service: SQL Server `OUTPUT` captures actual old/new values because direct
ExecuteUpdate/Delete bypass ChangeTracker. Bulk business/audit writes share an
explicit transaction.

## 4. Corresponding rows in AuditOperations

AuditTrail explains **what changed**. AuditOperations records **which logical
command completed**, including commands that produced no eligible audit rows.
Its Id equals the audit rows' OperationId.

| Id | Kind | ResultCount | Meaning |
| --- | --- | ---: | --- |
| O1 | Save | 3 | 1 supplier + 1 snapshot + 1 receipt |
| O2 | Save | 5 | 1 supplier + 2 field records + 1 manual event + 1 receipt |
| O3 | Save | 4 | 1 product + 2 field records + 1 receipt |
| O4 | Save | 3 | 1 deleted supplier + 1 snapshot + 1 receipt |
| O5 | BulkUpdate | 1 | 1 affected business row |
| O6 | BulkDelete | 1 | 1 affected business row |

Each receipt also stores TenantId, a command Fingerprint, and Timestamp. If SQL
commits but the acknowledgement is lost, a retry with the same operation identity
can verify that receipt instead of repeating the command. A different command
reusing that ID is rejected. This supports retry recovery; it is not automatic
idempotency for arbitrary HTTP requests.

[Full plan](audit-plan.md) · [Implementation detail](audit-implementation.md) ·
[AuditOperations explained](audit-operations.md)
