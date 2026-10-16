using AuditTrail.Application.Auditing;
using AuditTrail.Application.Catalog;
using AuditTrail.Application.Contracts;
using AuditTrail.Infrastructure.Auditing;
using AuditTrail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AuditTrail.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAuditTrailInfrastructure(this IServiceCollection services, string connectionString,
        Action<AuditOptions>? configure = null) => services.AddAuditTrailInfrastructure(_ => connectionString, configure);

    public static IServiceCollection AddAuditTrailInfrastructure(this IServiceCollection services, Func<IServiceProvider, string> connectionString,
        Action<AuditOptions>? configure = null)
    {
        var options = new AuditOptions(); configure?.Invoke(options);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<BackgroundAuditContext>();
        services.TryAddScoped<IAuditContextProvider>(sp => sp.GetRequiredService<BackgroundAuditContext>());
        services.TryAddScoped<IAuditAccessProvider, DenyAuditHistoryAccess>();
        services.AddSingleton<AuditPropertyPolicy>();
        services.AddSingleton<AuditValueSerializer>();
        services.AddSingleton<AuditTrailBuilder>();
        services.AddDbContextFactory<AppDbContext>((sp, o) => o.UseSqlServer(connectionString(sp),
            sql => sql.EnableRetryOnFailure(3)), ServiceLifetime.Scoped);
        services.AddScoped<ICatalogStore, CatalogStore>();
        services.AddScoped<IAuditTrailWriter, AuditTrailWriter>();
        services.AddScoped<IAuditedBulkOperations, SqlServerAuditedBulkOperations>();
        services.AddScoped<IAuditTrailQueryService, AuditTrailQueryService>();
        services.AddScoped<ICrudService<CategoryRequest, CategoryResponse>, CategoryService>();
        services.AddScoped<ICrudService<SupplierRequest, SupplierResponse>, SupplierService>();
        services.AddScoped<ProductService>();
        services.AddScoped<ICrudService<ProductRequest, ProductResponse>>(sp => sp.GetRequiredService<ProductService>());
        return services;
    }
}

internal sealed class DenyAuditHistoryAccess(IAuditContextProvider context) : IAuditAccessProvider
{
    public bool CanReadHistory => false;
    public string? TenantId => context.GetCurrent().TenantId;
}
