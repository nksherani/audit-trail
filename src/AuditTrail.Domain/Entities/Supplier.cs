namespace AuditTrail.Domain.Entities;

public class Supplier : AuditTrail.Domain.Common.AuditableEntity, AuditTrail.Domain.Common.ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? TenantId { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public string Name { get; set; } = string.Empty;
    [AuditTrail.Domain.Common.AuditMask]
    public string Email { get; set; } = string.Empty;
    [AuditTrail.Domain.Common.AuditIgnore]
    public string? InternalNote { get; set; }
    public ICollection<Product> Products { get; set; } = new List<Product>();
}
