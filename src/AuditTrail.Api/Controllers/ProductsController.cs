using System.ComponentModel.DataAnnotations;
using AuditTrail.Application.Catalog;
using AuditTrail.Application.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace AuditTrail.Api.Controllers;
[Route("api/products")]
public sealed class ProductsController(ICrudService<ProductRequest, ProductResponse> service,
    ProductService products) : CrudController<ProductRequest, ProductResponse>(service)
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
        => new { affected = await products.BulkUpdateAsync(request, ct) };
    [HttpPost("bulk-delete"), Authorize(Policy = "AuditWriter")]
    public async Task<object> BulkDelete(BulkDeleteRequest request, CancellationToken ct) =>
        new { affected = await products.BulkDeleteAsync(request, ct) };
}
public sealed record ReviewRequest([Required, StringLength(1000)] string Reason);
