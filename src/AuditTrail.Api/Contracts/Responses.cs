namespace AuditTrail.Api.Contracts;

public record CategoryResponse(int Id, string Name, string? Description);
public record SupplierResponse(int Id, string Name, string Email);
public record ProductResponse(int Id, string Name, decimal Price, int StockQuantity,
    int CategoryId, string CategoryName, int SupplierId, string SupplierName);
