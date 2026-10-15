using AuditTrail.Api.Contracts;
using AuditTrail.Api.Data;
using AuditTrail.Api.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuditTrail.Api.Controllers;

[ApiController]
[Route("api/products")]
public class ProductsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<ProductResponse>>> GetAll(CancellationToken ct) =>
        await Responses(db.Products.OrderBy(x => x.Id)).ToListAsync(ct);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProductResponse>> Get(int id, CancellationToken ct)
    {
        var product = await Responses(db.Products.Where(x => x.Id == id)).SingleOrDefaultAsync(ct);
        return product is null ? NotFound() : Ok(product);
    }

    [HttpPost]
    public async Task<ActionResult<ProductResponse>> Create(ProductRequest request, CancellationToken ct)
    {
        await ValidateReferences(request, ct);
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var product = new Product();
        Apply(product, request);
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        var response = await Responses(db.Products.Where(x => x.Id == product.Id)).SingleAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = product.Id }, response);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, ProductRequest request, CancellationToken ct)
    {
        var product = await db.Products.FindAsync([id], ct);
        if (product is null) return NotFound();
        await ValidateReferences(request, ct);
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        Apply(product, request);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var product = await db.Products.FindAsync([id], ct);
        if (product is null) return NotFound();
        db.Products.Remove(product);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static IQueryable<ProductResponse> Responses(IQueryable<Product> products) => products.AsNoTracking()
        .Select(x => new ProductResponse(x.Id, x.Name, x.Price, x.StockQuantity,
            x.CategoryId, x.Category.Name, x.SupplierId, x.Supplier.Name));

    private async Task ValidateReferences(ProductRequest request, CancellationToken ct)
    {
        if (!await db.Categories.AnyAsync(x => x.Id == request.CategoryId, ct))
            ModelState.AddModelError(nameof(request.CategoryId), "Category does not exist.");
        if (!await db.Suppliers.AnyAsync(x => x.Id == request.SupplierId, ct))
            ModelState.AddModelError(nameof(request.SupplierId), "Supplier does not exist.");
    }

    private static void Apply(Product product, ProductRequest request)
    {
        product.Name = request.Name.Trim();
        product.Price = request.Price;
        product.StockQuantity = request.StockQuantity;
        product.CategoryId = request.CategoryId;
        product.SupplierId = request.SupplierId;
    }
}
