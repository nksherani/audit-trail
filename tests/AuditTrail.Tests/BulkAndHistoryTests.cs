using AuditTrail.Application.Auditing;
using AuditTrail.Application.Catalog;
using AuditTrail.Application.Contracts;
using AuditTrail.Domain.Entities;
using AuditTrail.Infrastructure.Auditing;
using Microsoft.EntityFrameworkCore;

namespace AuditTrail.Tests;

[Collection("SQL Server")]
public sealed class BulkAndHistoryTests(SqlServerFixture fixture)
{
    private async Task<(string Tenant, Guid[] Ids)> SeedAsync(int count = 2)
    {
        await using var s = fixture.Open();
        for (int i = 0; i < count; i++) s.Db.Add(AutomaticAuditTests.NewProduct());
        await s.Db.SaveChangesAsync();
        return (s.Tenant!, await s.Db.Products.Select(x => x.Id).ToArrayAsync());
    }
    [Fact]
    public async Task Bulk_updates_capture_actual_old_values_and_replay_once()
    {
        var (tenant, ids) = await SeedAsync(); await using var s = fixture.Open(tenant);
        var spec = new AuditedBulkUpdateSpec<Product>(ids, new Dictionary<string, object?> { ["Price"] = 20m, ["StockQuantity"] = 8 }, OperationId: Guid.NewGuid());
        var bulk = s.Get<IAuditedBulkOperations>();
        Assert.Equal(2, await bulk.UpdateAsync(spec)); Assert.Equal(2, await bulk.UpdateAsync(spec));
        var rows = await s.Db.AuditTrails.Where(x => x.OperationId == spec.OperationId).ToListAsync();
        Assert.Equal(4, rows.Count); Assert.All(rows, x => Assert.Equal("Update", x.OperationType));
        Assert.All(rows.Where(x => x.ColumnName == "Price"), x => { Assert.Equal("10.00", x.OldValue); Assert.Equal("20.00", x.NewValue); });
        Assert.All(await s.Db.Products.AsNoTracking().ToListAsync(), x => Assert.Equal(20m, x.Price));
    }
    [Fact]
    public async Task Bulk_unchanged_values_produce_no_audit_and_soft_delete_is_field_level()
    {
        var (tenant, ids) = await SeedAsync(); await using var s = fixture.Open(tenant); var bulk = s.Get<IAuditedBulkOperations>();
        var noChange = Guid.NewGuid();
        await bulk.UpdateAsync(new AuditedBulkUpdateSpec<Product>(ids, new Dictionary<string, object?> { ["Price"] = 10m }, OperationId: noChange));
        Assert.False(await s.Db.AuditTrails.AnyAsync(x => x.OperationId == noChange));
        var deleted = Guid.NewGuid();
        await bulk.UpdateAsync(new AuditedBulkUpdateSpec<Product>(ids,
            new Dictionary<string, object?> { ["IsDeleted"] = true, ["DeletedAt"] = DateTimeOffset.UtcNow }, OperationId: deleted));
        Assert.Equal(4, await s.Db.AuditTrails.CountAsync(x => x.OperationId == deleted && x.OperationType == "Update" && x.ColumnName != null));
    }
    [Fact]
    public async Task Bulk_delete_has_one_snapshot_per_row_and_history_survives()
    {
        var (tenant, ids) = await SeedAsync(); await using var s = fixture.Open(tenant); var operation = Guid.NewGuid();
        Assert.Equal(2, await s.Get<IAuditedBulkOperations>().DeleteAsync(new AuditedBulkDeleteSpec<Product>(ids, OperationId: operation)));
        var records = await s.Db.AuditTrails.Where(x => x.OperationId == operation).ToListAsync();
        Assert.Equal(2, records.Count); Assert.All(records, x => { Assert.Null(x.NewValue); Assert.NotNull(x.OldValue); Assert.Null(x.ColumnName); });
        Assert.Empty(await s.Db.Products.AsNoTracking().ToListAsync());
    }
    [Fact]
    public async Task Failed_bulk_audit_copy_rolls_back_actual_business_change()
    {
        var (tenant, ids) = await SeedAsync();
        await using var s = fixture.Open(context: new("InvalidActor", Source: "BulkFailure", TenantId: tenant));
        var operation = Guid.NewGuid();
        await Assert.ThrowsAnyAsync<Exception>(() => s.Get<IAuditedBulkOperations>().UpdateAsync(new AuditedBulkUpdateSpec<Product>(ids,
            new Dictionary<string, object?> { ["Price"] = 999m }, OperationId: operation)));
        Assert.All(await s.Db.Products.AsNoTracking().ToListAsync(), x => Assert.Equal(10m, x.Price));
        Assert.False(await s.Db.AuditTrails.AnyAsync(x => x.OperationId == operation));
    }
    [Fact]
    public async Task Bulk_concurrency_and_unsupported_assignments_are_rejected()
    {
        var (tenant, ids) = await SeedAsync(); await using var s = fixture.Open(tenant); var bulk = s.Get<IAuditedBulkOperations>();
        await Assert.ThrowsAsync<NotSupportedException>(() => bulk.UpdateAsync(new AuditedBulkUpdateSpec<Product>(ids,
            new Dictionary<string, object?> { ["Id"] = Guid.NewGuid() })));
        await Assert.ThrowsAsync<ArgumentException>(() => bulk.UpdateAsync(new AuditedBulkUpdateSpec<Product>(ids,
            new Dictionary<string, object?> { ["Unknown"] = "bad" })));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => bulk.UpdateAsync(new AuditedBulkUpdateSpec<Product>(ids,
            new Dictionary<string, object?> { ["Price"] = 999m }, ExpectedVersions: ids.ToDictionary(x => x, _ => new byte[8]))));
        Assert.All(await s.Db.Products.AsNoTracking().ToListAsync(), x => Assert.Equal(10m, x.Price));
    }
    [Fact]
    public async Task Bulk_scope_rejects_stale_tracked_entities()
    {
        var (tenant, ids) = await SeedAsync(); await using var s = fixture.Open(tenant);
        await s.Db.Products.FirstAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<IAuditedBulkOperations>().DeleteAsync(new AuditedBulkDeleteSpec<Product>(ids)));
    }
    [Fact]
    public async Task Bulk_masking_is_consistent_for_updates_and_deletes()
    {
        await using var seed = fixture.Open(); var supplier = new Supplier { Name = "Mask bulk", Email = "first@example.com", InternalNote = "secret" };
        seed.Db.Add(supplier); await seed.Db.SaveChangesAsync();
        await using var s = fixture.Open(seed.Tenant); var bulk = s.Get<IAuditedBulkOperations>();
        await bulk.UpdateAsync(new AuditedBulkUpdateSpec<Supplier>([supplier.Id], new Dictionary<string, object?> { ["Email"] = "second@example.com" }));
        await bulk.DeleteAsync(new AuditedBulkDeleteSpec<Supplier>([supplier.Id]));
        var rows = await s.Db.AuditTrails.Where(x => x.PkId == supplier.Id).ToListAsync();
        Assert.All(rows, x => { Assert.DoesNotContain("example.com", (x.OldValue ?? "") + x.NewValue); Assert.DoesNotContain("InternalNote", (x.OldValue ?? "") + x.NewValue); });
    }
    [Fact]
    public async Task Standalone_manual_events_are_independent_and_redact_metadata()
    {
        await using var s = fixture.Open(context: new("System", Source: "AzureFunction", CorrelationId: "function-event", TenantId: Guid.NewGuid().ToString("N")));
        s.Db.Add(new Category { Name = "Not saved" });
        await s.Get<IAuditTrailWriter>().WriteStandaloneAsync(new() { EntityName = "Reports", EventName = "ReportExported",
            Metadata = new Dictionary<string, object?> { ["AccessToken"] = "private-token", ["Rows"] = 12 } });
        Assert.False(await s.Db.Categories.AsNoTracking().AnyAsync());
        var audit = await s.Db.AuditTrails.SingleAsync(x => x.TenantId == s.Tenant);
        Assert.Equal("Manual", audit.AuditRecordType); Assert.Equal("AzureFunction", audit.Source); Assert.Null(audit.PkId);
        Assert.DoesNotContain("private-token", audit.Metadata);
    }
    [Fact]
    public async Task Query_filters_chronology_grouping_and_authorization_are_tenant_scoped()
    {
        var info = new AuditContextInfo("User", "reader", "WebAPI", "query-correlation", Guid.NewGuid().ToString("N"));
        await using var s = fixture.Open(context: info); var entity = new Category { Name = "Before", Description = "Old" };
        s.Db.Add(entity); await s.Db.SaveChangesAsync(); entity.Name = "After"; entity.Description = "New"; await s.Db.SaveChangesAsync();
        var query = s.Get<IAuditTrailQueryService>();
        var page = await query.SearchAsync(new() { PkId = entity.Id, CorrelationId = "query-correlation", UserId = "reader", PageSize = 2 });
        Assert.Equal(3, page.TotalCount); Assert.Equal(2, page.Items.Count); Assert.Equal("Insert", page.Items[0].OperationType);
        var groups = await query.GroupedAsync(new() { PkId = entity.Id, PageSize = 1, Page = 2 });
        Assert.Equal(2, groups.TotalCount); Assert.Equal(2, Assert.Single(groups.Items).Records.Count);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => query.SearchAsync(new() { TenantId = "another-tenant" }));
        await using var other = fixture.Open();
        Assert.Empty((await other.Get<IAuditTrailQueryService>().SearchAsync(new() { PkId = entity.Id })).Items);
        Assert.Null(await other.Db.Categories.SingleOrDefaultAsync(x => x.Id == entity.Id));
    }
    [Fact]
    public async Task Cross_tenant_bulk_and_manual_entity_references_do_not_leak()
    {
        var (tenant, ids) = await SeedAsync(); await using var s = fixture.Open();
        Assert.Equal(0, await s.Get<IAuditedBulkOperations>().DeleteAsync(new AuditedBulkDeleteSpec<Product>(ids)));
        s.Get<IAuditTrailWriter>().AddManual(new() { EntityName = typeof(Product).FullName!, PkId = ids[0], EventName = "UnauthorizedReview" });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Db.SaveChangesAsync());
    }
    [Fact]
    public async Task Manual_operation_override_is_respected_and_field_masking_is_enforced()
    {
        await using var s = fixture.Open(); var supplier = new Supplier { Name = "Manual mask", Email = "private@example.com" };
        s.Db.Add(supplier); await s.Db.SaveChangesAsync();
        var operation = Guid.NewGuid();
        s.Get<IAuditTrailWriter>().AddManual(new() { EntityName = typeof(Supplier).FullName!, PkId = supplier.Id,
            EventName = "EmailReviewed", ColumnName = "Email", OldValue = "private@example.com", NewValue = "other@example.com", OperationId = operation });
        await s.Db.SaveChangesAsync();
        var record = await s.Db.AuditTrails.SingleAsync(x => x.OperationId == operation);
        Assert.Equal("Manual", record.AuditRecordType); Assert.Equal("String", record.DataType);
        Assert.Contains("[REDACTED]", record.NewValue); Assert.DoesNotContain("example.com", record.OldValue);
    }
    [Fact]
    public async Task Application_bulk_soft_delete_replays_without_timestamp_drift()
    {
        var (tenant, ids) = await SeedAsync(); await using var s = fixture.Open(tenant);
        var command = new BulkProductRequest(ids, null, null, true, Guid.NewGuid());
        var service = s.Get<ProductService>();
        Assert.Equal(2, await service.BulkUpdateAsync(command, default));
        Assert.Equal(2, await service.BulkUpdateAsync(command, default));
        Assert.Equal(4, await s.Db.AuditTrails.CountAsync(x => x.OperationId == command.OperationId));
    }
}
