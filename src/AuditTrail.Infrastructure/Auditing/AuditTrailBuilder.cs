using AuditTrail.Application.Auditing;
using AuditTrail.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AuditTrail.Infrastructure.Auditing;

public sealed class AuditTrailBuilder(AuditPropertyPolicy policy, AuditValueSerializer serializer)
{
    public AuditRecord NewRecord(string entityName, Guid? id, string operation, Guid operationId,
        AuditContextInfo context, DateTimeOffset timestamp) => new()
    {
        PkId = id, EntityName = entityName, OperationType = operation, OperationId = operationId,
        Timestamp = timestamp.ToUniversalTime(), TriggeredBy = context.TriggeredBy, UserId = context.UserId,
        Source = context.Source, CorrelationId = context.CorrelationId, TenantId = context.TenantId, Reason = context.Reason
    };

    public List<AuditRecord> Build(EntityEntry entry, Guid operationId, AuditContextInfo context, DateTimeOffset timestamp)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key?.Properties.Count != 1 || key.Properties[0].ClrType != typeof(Guid) ||
            entry.Property(key.Properties[0].Name).CurrentValue is not Guid id || id == Guid.Empty ||
            key.Properties[0].ValueGenerated != ValueGenerated.Never || entry.Property(key.Properties[0].Name).IsTemporary)
            throw new InvalidOperationException("Auditable entities require a single preassigned, nonempty GUID key with ValueGeneratedNever.");
        if (entry.Metadata.GetComplexProperties().Any() || entry.Metadata.GetNavigations().Any(x => x.TargetEntityType.IsOwned()))
            throw new NotSupportedException("Owned/complex entity graphs require an explicit scalar JSON mapping before auditing.");
        var properties = entry.Metadata.GetProperties().Where(x => policy.Resolve(x) != AuditDisposition.Ignore).ToArray();
        foreach (var property in properties)
        {
            // Pre-save snapshots must not claim precision that SQL Server will round away.
            if (property.GetScale() is int scale && entry.Property(property.Name).CurrentValue is decimal amount &&
                decimal.Round(amount, scale, MidpointRounding.AwayFromZero) != amount)
                throw new InvalidOperationException($"{property.Name} exceeds the mapped decimal scale ({scale}).");
        }
        var name = entry.Metadata.ClrType.FullName!;
        if (entry.State is EntityState.Added or EntityState.Deleted)
        {
            var deleting = entry.State == EntityState.Deleted;
            var values = properties.ToDictionary(x => x.Name,
                x => deleting ? entry.Property(x.Name).OriginalValue : entry.Property(x.Name).CurrentValue);
            return [Snapshot(entry.Metadata, id, values, deleting, operationId, context, timestamp)];
        }
        var result = new List<AuditRecord>();
        foreach (var property in properties.Where(x => !x.IsPrimaryKey()))
        {
            var tracked = entry.Property(property.Name);
            if (!tracked.IsModified || AuditValueSerializer.Equal(property, tracked.OriginalValue, tracked.CurrentValue)) continue;
            result.Add(Change(entry.Metadata, id, property, tracked.OriginalValue, tracked.CurrentValue, operationId, context, timestamp));
        }
        return result;
    }

    public AuditRecord Snapshot(IEntityType entity, Guid id, IReadOnlyDictionary<string, object?> values,
        bool deleting, Guid operationId, AuditContextInfo context, DateTimeOffset timestamp)
    {
        var snapshot = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in entity.GetProperties())
        {
            var disposition = policy.Resolve(property);
            if (disposition == AuditDisposition.Ignore) continue;
            snapshot[property.Name] = serializer.SnapshotValue(property, values[property.Name], disposition);
        }
        var record = NewRecord(entity.ClrType.FullName!, id, deleting ? "Delete" : "Insert", operationId, context, timestamp);
        record.DataType = "json";
        if (deleting) record.OldValue = serializer.Snapshot(snapshot); else record.NewValue = serializer.Snapshot(snapshot);
        return record;
    }

    public AuditRecord Change(IEntityType entity, Guid id, IProperty property, object? oldValue, object? newValue,
        Guid operationId, AuditContextInfo context, DateTimeOffset timestamp)
    {
        var record = NewRecord(entity.ClrType.FullName!, id, "Update", operationId, context, timestamp);
        record.ColumnName = property.Name;
        record.DataType = AuditValueSerializer.TypeName(property);
        var mask = policy.Resolve(property) == AuditDisposition.Mask;
        record.OldValue = serializer.Serialize(mask && oldValue is not null ? AuditValueSerializer.Mask : oldValue,
            property.PropertyInfo?.IsDefined(typeof(AuditJsonAttribute), true) == true);
        record.NewValue = serializer.Serialize(mask && newValue is not null ? AuditValueSerializer.Mask : newValue,
            property.PropertyInfo?.IsDefined(typeof(AuditJsonAttribute), true) == true);
        return record;
    }

    public AuditRecord Manual(AuditEvent auditEvent, IModel model, Guid operationId, AuditContextInfo context, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(auditEvent.EntityName) || string.IsNullOrWhiteSpace(auditEvent.EventName) || auditEvent.PkId == Guid.Empty)
            throw new ArgumentException("Manual events require an entity/event name and a nonempty ID when supplied.");
        if (auditEvent.OperationId is Guid requested && requested != operationId)
            throw new InvalidOperationException("Manual events in one save must share its operation ID. Set trusted context.OperationId to reuse an ID.");
        var record = NewRecord(auditEvent.EntityName, auditEvent.PkId, "Custom", operationId, context, timestamp);
        record.AuditRecordType = "Manual";
        record.EventName = auditEvent.EventName;
        record.Reason = auditEvent.Reason ?? context.Reason;
        record.Description = auditEvent.Description is null ? null : serializer.Check(auditEvent.Description);
        record.CorrelationId = auditEvent.CorrelationId ?? context.CorrelationId;
        record.ColumnName = auditEvent.ColumnName;
        record.DataType = auditEvent.ColumnName is null ? "json" : "manual";
        var property = model.GetEntityTypes().SingleOrDefault(x => x.ClrType.FullName == auditEvent.EntityName)?.FindProperty(auditEvent.ColumnName ?? "");
        if (auditEvent.ColumnName is not null && property is null)
            throw new ArgumentException("Manual field changes must identify a mapped property by its full entity name.");
        var disposition = property is null ? AuditDisposition.Include : policy.Resolve(property);
        if (property is not null) record.DataType = AuditValueSerializer.TypeName(property);
        if (disposition == AuditDisposition.Ignore && (auditEvent.OldValue is not null || auditEvent.NewValue is not null))
            throw new InvalidOperationException("Cannot manually audit an ignored field.");
        // Arbitrary snapshot payloads are prohibited: manual structured values go through redacted metadata.
        if (property is null && (auditEvent.OldValue is not null || auditEvent.NewValue is not null))
            throw new ArgumentException("Manual old/new values require an eligible mapped ColumnName; use Metadata for structured event context.");
        var json = property?.PropertyInfo?.IsDefined(typeof(AuditJsonAttribute), true) == true;
        record.OldValue = serializer.Serialize(disposition == AuditDisposition.Mask && auditEvent.OldValue is not null ? AuditValueSerializer.Mask : auditEvent.OldValue, json);
        record.NewValue = serializer.Serialize(disposition == AuditDisposition.Mask && auditEvent.NewValue is not null ? AuditValueSerializer.Mask : auditEvent.NewValue, json);
        record.Metadata = serializer.Metadata(auditEvent.Metadata);
        return record;
    }
}
