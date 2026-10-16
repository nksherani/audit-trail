namespace AuditTrail.Domain.Entities;

// Deliberately not auditable: operational checkpoint, not a business entity.
public sealed class ImportCheckpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Cursor { get; set; } = "";
}
