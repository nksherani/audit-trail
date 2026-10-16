using System.ComponentModel.DataAnnotations;

namespace AuditTrail.Application.Contracts;

public record CategoryRequest(
    [Required, StringLength(100)] string Name,
    [StringLength(500)] string? Description);

public record SupplierRequest(
    [Required, StringLength(150)] string Name,
    [Required, EmailAddress, StringLength(254)] string Email);

public record ProductRequest(
    [Required, StringLength(150)] string Name,
    [Range(typeof(decimal), "0", "9999999999999999.99")] decimal Price,
    [Range(0, int.MaxValue)] int StockQuantity,
    Guid CategoryId,
    Guid SupplierId);
