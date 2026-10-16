using AuditTrail.Application.Catalog;
using AuditTrail.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuditTrail.Infrastructure.Persistence;

public sealed class CatalogStore(AppDbContext db) : ICatalogStore
{
    private IQueryable<T> Query<T>() where T : class => typeof(T) == typeof(Product)
        ? (IQueryable<T>)db.Products.Include(x => x.Category).Include(x => x.Supplier)
        : db.Set<T>();
    public Task<List<T>> ListAsync<T>(CancellationToken ct) where T : class => Query<T>().OrderBy(x => EF.Property<Guid>(x, "Id")).ToListAsync(ct);
    public Task<T?> FindAsync<T>(Guid id, CancellationToken ct) where T : class => Query<T>().SingleOrDefaultAsync(x => EF.Property<Guid>(x, "Id") == id, ct);
    public Task<bool> HasProductsAsync(Guid? categoryId, Guid? supplierId, CancellationToken ct) =>
        db.Products.AnyAsync(x => (categoryId == null || x.CategoryId == categoryId) && (supplierId == null || x.SupplierId == supplierId), ct);
    public void Add<T>(T entity) where T : class => db.Set<T>().Add(entity);
    public void Remove<T>(T entity) where T : class => db.Set<T>().Remove(entity);
    public async Task<int> SaveAsync(CancellationToken ct)
    {
        try { return await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new BusinessConflictException("The entity changed concurrently. Reload it before retrying."); }
    }
}
