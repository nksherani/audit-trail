using AuditTrail.Application.Auditing;
using AuditTrail.Application.Contracts;
using AuditTrail.Domain.Entities;

namespace AuditTrail.Application.Catalog;

public sealed class EntityNotFoundException : Exception;
public sealed class BusinessConflictException(string message) : Exception(message);
public sealed class BusinessValidationException(string message) : Exception(message);

public interface ICatalogStore
{
    Task<List<T>> ListAsync<T>(CancellationToken ct) where T : class;
    Task<T?> FindAsync<T>(Guid id, CancellationToken ct) where T : class;
    Task<bool> HasProductsAsync(Guid? categoryId, Guid? supplierId, CancellationToken ct);
    void Add<T>(T entity) where T : class;
    void Remove<T>(T entity) where T : class;
    Task<int> SaveAsync(CancellationToken ct);
}
public interface ICrudService<TRequest, TResponse>
{
    Task<List<TResponse>> ListAsync(CancellationToken ct);
    Task<TResponse> GetAsync(Guid id, CancellationToken ct);
    Task<TResponse> CreateAsync(TRequest request, CancellationToken ct);
    Task UpdateAsync(Guid id, TRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}
public abstract class CrudService<TEntity, TRequest, TResponse>(ICatalogStore store)
    : ICrudService<TRequest, TResponse> where TEntity : class, new()
{
    protected ICatalogStore Store => store;
    protected abstract TResponse Map(TEntity entity);
    protected abstract Task ApplyAsync(TEntity entity, TRequest request, CancellationToken ct);
    protected virtual Task BeforeDeleteAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
    public async Task<List<TResponse>> ListAsync(CancellationToken ct) => (await store.ListAsync<TEntity>(ct)).Select(Map).ToList();
    public async Task<TResponse> GetAsync(Guid id, CancellationToken ct) => Map(await Find(id, ct));
    protected async Task<TEntity> Find(Guid id, CancellationToken ct) =>
        await store.FindAsync<TEntity>(id, ct) ?? throw new EntityNotFoundException();
    public async Task<TResponse> CreateAsync(TRequest request, CancellationToken ct)
    {
        var entity = new TEntity();
        await ApplyAsync(entity, request, ct);
        store.Add(entity);
        await store.SaveAsync(ct);
        return Map(entity);
    }
    public async Task UpdateAsync(Guid id, TRequest request, CancellationToken ct)
    {
        await ApplyAsync(await Find(id, ct), request, ct);
        await store.SaveAsync(ct);
    }
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var entity = await Find(id, ct);
        await BeforeDeleteAsync(id, ct);
        store.Remove(entity);
        await store.SaveAsync(ct);
    }
}
public sealed class CategoryService(ICatalogStore store) : CrudService<Category, CategoryRequest, CategoryResponse>(store)
{
    protected override CategoryResponse Map(Category x) => new(x.Id, x.Name, x.Description);
    protected override Task ApplyAsync(Category x, CategoryRequest r, CancellationToken ct)
    { x.Name = r.Name.Trim(); x.Description = r.Description?.Trim(); return Task.CompletedTask; }
    protected override async Task BeforeDeleteAsync(Guid id, CancellationToken ct)
    {
        if (await Store.HasProductsAsync(id, null, ct)) throw new BusinessConflictException("Delete or reassign this category's products first.");
    }
}
public sealed class SupplierService(ICatalogStore store) : CrudService<Supplier, SupplierRequest, SupplierResponse>(store)
{
    protected override SupplierResponse Map(Supplier x) => new(x.Id, x.Name, x.Email);
    protected override Task ApplyAsync(Supplier x, SupplierRequest r, CancellationToken ct)
    { x.Name = r.Name.Trim(); x.Email = r.Email.Trim(); return Task.CompletedTask; }
    protected override async Task BeforeDeleteAsync(Guid id, CancellationToken ct)
    {
        if (await Store.HasProductsAsync(null, id, ct)) throw new BusinessConflictException("Delete or reassign this supplier's products first.");
    }
}
public sealed class ProductService(ICatalogStore store, IAuditTrailWriter writer)
    : CrudService<Product, ProductRequest, ProductResponse>(store)
{
    protected override ProductResponse Map(Product x) => new(x.Id, x.Name, x.Price, x.StockQuantity,
        x.CategoryId, x.Category.Name, x.SupplierId, x.Supplier.Name);
    protected override async Task ApplyAsync(Product x, ProductRequest r, CancellationToken ct)
    {
        x.Category = await Store.FindAsync<Category>(r.CategoryId, ct) ?? throw new BusinessValidationException("Category does not exist.");
        x.Supplier = await Store.FindAsync<Supplier>(r.SupplierId, ct) ?? throw new BusinessValidationException("Supplier does not exist.");
        x.Name = r.Name.Trim(); x.Price = r.Price; x.StockQuantity = r.StockQuantity;
        x.CategoryId = r.CategoryId; x.SupplierId = r.SupplierId;
    }
    public async Task SetDeletedAsync(Guid id, bool deleted, CancellationToken ct)
    {
        var entity = await Find(id, ct);
        entity.IsDeleted = deleted;
        entity.DeletedAt = deleted ? DateTimeOffset.UtcNow : null;
        await Store.SaveAsync(ct);
    }
    public async Task ReviewAsync(Guid id, string reason, CancellationToken ct)
    {
        await Find(id, ct);
        writer.AddManual(new AuditEvent { EntityName = typeof(Product).FullName!, PkId = id,
            EventName = "ProductReviewed", Reason = reason, Description = "Product reviewed by an authorized operator." });
        await Store.SaveAsync(ct);
    }
}
