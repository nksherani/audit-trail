using AuditTrail.Api.Contracts;
using AuditTrail.Api.Data;
using AuditTrail.Api.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuditTrail.Api.Controllers;

[ApiController]
[Route("api/categories")]
public class CategoriesController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<CategoryResponse>>> GetAll(CancellationToken ct) =>
        await db.Categories.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new CategoryResponse(x.Id, x.Name, x.Description)).ToListAsync(ct);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<CategoryResponse>> Get(int id, CancellationToken ct)
    {
        var category = await db.Categories.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return category is null ? NotFound() : Ok(ToResponse(category));
    }

    [HttpPost]
    public async Task<ActionResult<CategoryResponse>> Create(CategoryRequest request, CancellationToken ct)
    {
        var category = new Category { Name = request.Name.Trim(), Description = request.Description?.Trim() };
        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = category.Id }, ToResponse(category));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, CategoryRequest request, CancellationToken ct)
    {
        var category = await db.Categories.FindAsync([id], ct);
        if (category is null) return NotFound();
        category.Name = request.Name.Trim();
        category.Description = request.Description?.Trim();
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var category = await db.Categories.FindAsync([id], ct);
        if (category is null) return NotFound();
        if (await db.Products.AnyAsync(x => x.CategoryId == id, ct))
            return Conflict(new ProblemDetails { Status = 409, Detail = "Delete or reassign this category's products first." });
        db.Categories.Remove(category);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static CategoryResponse ToResponse(Category category) =>
        new(category.Id, category.Name, category.Description);
}
