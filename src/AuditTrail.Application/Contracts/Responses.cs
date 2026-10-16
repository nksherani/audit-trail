namespace AuditTrail.Application.Contracts;

public record CategoryResponse(Guid Id, string Name, string? Description);
public record SupplierResponse(Guid Id, string Name, string Email);
public record ProductResponse(Guid Id, string Name, decimal Price, int StockQuantity,
    Guid CategoryId, string CategoryName, Guid SupplierId, string SupplierName);
