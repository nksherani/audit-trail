namespace AuditTrail.Domain.Entities;

public class Product : AuditTrail.Domain.Common.IAuditableEntity, AuditTrail.Domain.Common.ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? TenantId { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
    public Guid CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
}
