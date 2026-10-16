namespace AuditTrail.Domain.Entities;

public class Category : AuditTrail.Domain.Common.IAuditableEntity, AuditTrail.Domain.Common.ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? TenantId { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ICollection<Product> Products { get; set; } = new List<Product>();
}
