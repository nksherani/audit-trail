using AuditTrail.Api.Contracts;
using AuditTrail.Api.Data;
using AuditTrail.Api.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuditTrail.Api.Controllers;

[ApiController]
[Route("api/suppliers")]
public class SuppliersController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<SupplierResponse>>> GetAll(CancellationToken ct) =>
        await db.Suppliers.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new SupplierResponse(x.Id, x.Name, x.Email)).ToListAsync(ct);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SupplierResponse>> Get(int id, CancellationToken ct)
    {
        var supplier = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return supplier is null ? NotFound() : Ok(ToResponse(supplier));
    }

    [HttpPost]
    public async Task<ActionResult<SupplierResponse>> Create(SupplierRequest request, CancellationToken ct)
    {
        var supplier = new Supplier { Name = request.Name.Trim(), Email = request.Email.Trim() };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = supplier.Id }, ToResponse(supplier));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, SupplierRequest request, CancellationToken ct)
    {
        var supplier = await db.Suppliers.FindAsync([id], ct);
        if (supplier is null) return NotFound();
        supplier.Name = request.Name.Trim();
        supplier.Email = request.Email.Trim();
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var supplier = await db.Suppliers.FindAsync([id], ct);
        if (supplier is null) return NotFound();
        if (await db.Products.AnyAsync(x => x.SupplierId == id, ct))
            return Conflict(new ProblemDetails { Status = 409, Detail = "Delete or reassign this supplier's products first." });
        db.Suppliers.Remove(supplier);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static SupplierResponse ToResponse(Supplier supplier) => new(supplier.Id, supplier.Name, supplier.Email);
}
