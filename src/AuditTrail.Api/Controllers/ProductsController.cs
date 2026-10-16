using System.ComponentModel.DataAnnotations;
using AuditTrail.Application.Auditing;
using AuditTrail.Application.Catalog;
using AuditTrail.Application.Contracts;
using AuditTrail.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace AuditTrail.Api.Controllers;
[Route("api/products")]
public sealed class ProductsController(ICrudService<ProductRequest, ProductResponse> service,
    ProductService products, IAuditedBulkOperations bulk) : CrudController<ProductRequest, ProductResponse>(service)
{
    protected override Guid Id(ProductResponse response) => response.Id;
    [HttpPost("{id:guid}/soft-delete")]
    public async Task<IActionResult> SoftDelete(Guid id, CancellationToken ct)
    { await products.SetDeletedAsync(id, true, ct); return NoContent(); }
    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    { await products.SetDeletedAsync(id, false, ct); return NoContent(); }
    [HttpPost("{id:guid}/review"), Authorize(Policy = "AuditWriter")]
    public async Task<IActionResult> Review(Guid id, ReviewRequest request, CancellationToken ct)
    { await products.ReviewAsync(id, request.Reason, ct); return NoContent(); }
    [HttpPost("bulk-update"), Authorize(Policy = "AuditWriter")]
    public async Task<object> BulkUpdate(BulkProductRequest request, CancellationToken ct)
    {
        var changes = new Dictionary<string, object?>();
        if (request.Price is not null) changes[nameof(Product.Price)] = request.Price.Value;
        if (request.StockQuantity is not null) changes[nameof(Product.StockQuantity)] = request.StockQuantity.Value;
        if (request.IsDeleted is not null)
        { changes[nameof(Product.IsDeleted)] = request.IsDeleted.Value; changes[nameof(Product.DeletedAt)] = request.IsDeleted.Value ? DateTimeOffset.UtcNow : null; }
        return new { affected = await bulk.UpdateAsync(new AuditedBulkUpdateSpec<Product>(request.Ids, changes, OperationId: request.OperationId), ct) };
    }
    [HttpPost("bulk-delete"), Authorize(Policy = "AuditWriter")]
    public async Task<object> BulkDelete(BulkDeleteRequest request, CancellationToken ct) =>
        new { affected = await bulk.DeleteAsync(new AuditedBulkDeleteSpec<Product>(request.Ids, OperationId: request.OperationId), ct) };
}
public sealed record ReviewRequest([Required, StringLength(1000)] string Reason);
public sealed record BulkDeleteRequest([Required] Guid[] Ids, Guid? OperationId);
public sealed record BulkProductRequest([Required] Guid[] Ids, [Range(typeof(decimal), "0", "9999999999999999.99")] decimal? Price,
    [Range(0, int.MaxValue)] int? StockQuantity, bool? IsDeleted, Guid? OperationId);
