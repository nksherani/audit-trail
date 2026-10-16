namespace AuditTrail.Infrastructure.Auditing;

// Persistence model; deliberately does not implement IAuditableEntity.
public sealed class AuditRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? PkId { get; set; }
    public string EntityName { get; set; } = "";
    public string? ColumnName { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? DataType { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string TriggeredBy { get; set; } = "System";
    public string? UserId { get; set; }
    public string AuditRecordType { get; set; } = "Auto";
    public string OperationType { get; set; } = "Insert";
    public Guid OperationId { get; set; }
    public string? CorrelationId { get; set; }
    public string? Source { get; set; }
    public string? EventName { get; set; }
    public string? Reason { get; set; }
    public string? Description { get; set; }
    public string? TenantId { get; set; }
    public string? Metadata { get; set; }
    public short AuditSchemaVersion { get; set; } = 1;
}

// Transactional receipt used to verify uncertain commits and replay explicit operation IDs.
public sealed class AuditOperation
{
    public Guid Id { get; set; }
    public string? TenantId { get; set; }
    public string Kind { get; set; } = "Save";
    public string Fingerprint { get; set; } = "";
    public int ResultCount { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
