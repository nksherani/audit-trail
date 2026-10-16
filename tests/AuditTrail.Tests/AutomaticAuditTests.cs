using System.Text.Json;
using AuditTrail.Application.Auditing;
using AuditTrail.Domain.Entities;
using AuditTrail.Infrastructure.Auditing;
using Microsoft.EntityFrameworkCore;

namespace AuditTrail.Tests;

[Collection("SQL Server")]
public sealed class AutomaticAuditTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Insert_is_one_snapshot_with_preassigned_guid_and_no_navigation_or_rowversion()
    {
        await using var s = fixture.Open();
        var entity = new Category { Name = "Insert", Description = "Initial" };
        var idBeforeSave = entity.Id;
        s.Db.Add(entity);
        await s.Db.SaveChangesAsync();
        var audit = Assert.Single(await s.Db.AuditTrails.Where(x => x.PkId == entity.Id).ToListAsync());
        Assert.Equal(idBeforeSave, audit.PkId);
        Assert.Equal("Insert", audit.OperationType);
        Assert.Null(audit.OldValue); Assert.Null(audit.ColumnName); Assert.Equal("json", audit.DataType);
        using var json = JsonDocument.Parse(audit.NewValue!);
        Assert.Equal(entity.Id, json.RootElement.GetProperty("Id").GetGuid());
        Assert.False(json.RootElement.TryGetProperty("Products", out _));
        Assert.False(json.RootElement.TryGetProperty("RowVersion", out _));
        Assert.Equal(TimeSpan.Zero, audit.Timestamp.Offset);
    }
    [Fact]
    public async Task Two_effective_updates_share_operation_and_unchanged_modified_fields_are_ignored()
    {
        await using var s = fixture.Open();
        var entity = new Category { Name = "Before", Description = "Old" };
        s.Db.Add(entity); await s.Db.SaveChangesAsync();
        entity.Name = "After"; entity.Description = "New";
        await s.Db.SaveChangesAsync();
        var rows = await s.Db.AuditTrails.Where(x => x.PkId == entity.Id && x.OperationType == "Update").ToListAsync();
        Assert.Equal(2, rows.Count); Assert.Single(rows.Select(x => x.OperationId).Distinct());
        Assert.Equal("\"Before\"", rows.Single(x => x.ColumnName == "Name").OldValue);
        Assert.Equal("\"After\"", rows.Single(x => x.ColumnName == "Name").NewValue);
        s.Db.Entry(entity).Property(x => x.Name).IsModified = true;
        await s.Db.SaveChangesAsync();
        Assert.Equal(2, await s.Db.AuditTrails.CountAsync(x => x.PkId == entity.Id && x.OperationType == "Update"));
    }
    [Fact]
    public async Task All_entities_in_a_save_share_operation_and_manual_context()
    {
        var tenant = Guid.NewGuid().ToString("N");
        await using var s = fixture.Open(context: new("User", "reviewer-1", "WebAPI", "correlation-1", tenant, "Reviewed"));
        var category = new Category { Name = "Category" }; var supplier = new Supplier { Name = "Supplier", Email = "private@example.com", InternalNote = "secret-note" };
        s.Db.AddRange(category, supplier);
        s.Get<IAuditTrailWriter>().AddManual(new() { EntityName = typeof(Category).FullName!, PkId = category.Id, EventName = "CategoryReviewed" });
        await s.Db.SaveChangesAsync();
        var rows = await s.Db.AuditTrails.Where(x => x.TenantId == tenant).ToListAsync();
        Assert.Equal(3, rows.Count); Assert.Single(rows.Select(x => x.OperationId).Distinct());
        Assert.All(rows, x => { Assert.Equal("reviewer-1", x.UserId); Assert.Equal("User", x.TriggeredBy); Assert.Equal("correlation-1", x.CorrelationId); });
        var snapshot = rows.Single(x => x.PkId == supplier.Id).NewValue!;
        Assert.DoesNotContain("private@example.com", snapshot); Assert.Contains("[REDACTED]", snapshot); Assert.DoesNotContain("InternalNote", snapshot);
    }
    [Fact]
    public async Task Mask_and_ignore_apply_to_updates_and_hard_delete_snapshots()
    {
        await using var s = fixture.Open();
        var supplier = new Supplier { Name = "Supplier", Email = "first@example.com", InternalNote = "secret" };
        s.Db.Add(supplier); await s.Db.SaveChangesAsync();
        supplier.Email = "second@example.com"; supplier.InternalNote = "other-secret";
        await s.Db.SaveChangesAsync();
        var update = Assert.Single(await s.Db.AuditTrails.Where(x => x.PkId == supplier.Id && x.OperationType == "Update").ToListAsync());
        Assert.Equal("Email", update.ColumnName); Assert.Equal(update.OldValue, update.NewValue); Assert.Contains("[REDACTED]", update.NewValue);
        s.Db.Remove(supplier); await s.Db.SaveChangesAsync();
        var deleted = await s.Db.AuditTrails.SingleAsync(x => x.PkId == supplier.Id && x.OperationType == "Delete");
        Assert.Null(deleted.NewValue); Assert.Null(deleted.ColumnName);
        Assert.DoesNotContain("second@example.com", deleted.OldValue); Assert.DoesNotContain("InternalNote", deleted.OldValue);
    }
    [Fact]
    public async Task Soft_delete_and_restore_are_field_level_updates()
    {
        await using var s = fixture.Open();
        var product = NewProduct(); s.Db.Add(product); await s.Db.SaveChangesAsync();
        product.IsDeleted = true; product.DeletedAt = DateTimeOffset.UtcNow;
        await s.Db.SaveChangesAsync();
        product.IsDeleted = false; product.DeletedAt = null;
        await s.Db.SaveChangesAsync();
        var updates = await s.Db.AuditTrails.Where(x => x.PkId == product.Id && x.OperationType == "Update").ToListAsync();
        Assert.Equal(4, updates.Count); Assert.All(updates, x => Assert.NotNull(x.ColumnName));
        Assert.Equal(2, updates.Select(x => x.OperationId).Distinct().Count());
    }
    [Fact]
    public async Task Non_auditable_and_unchanged_entities_have_no_audit_rows()
    {
        await using var s = fixture.Open();
        var checkpoint = new ImportCheckpoint { Cursor = "offset-1" };
        s.Db.Add(checkpoint); await s.Db.SaveChangesAsync();
        Assert.Equal(0, await s.Db.SaveChangesAsync());
        Assert.False(await s.Db.AuditTrails.AnyAsync(x => x.PkId == checkpoint.Id));
    }
    [Fact]
    public async Task Failed_audit_rolls_back_business_and_retry_does_not_duplicate_staged_rows()
    {
        await using var s = fixture.Open(context: new("InvalidActor", Source: "FailureTest", TenantId: Guid.NewGuid().ToString("N")));
        var entity = new Category { Name = "Retry" }; s.Db.Add(entity);
        await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        Assert.False(await s.Db.Categories.AsNoTracking().AnyAsync(x => x.Id == entity.Id));
        Assert.False(await s.Db.AuditTrails.AnyAsync(x => x.PkId == entity.Id));
        Assert.Empty(s.Db.ChangeTracker.Entries<AuditRecord>());
        s.Get<BackgroundAuditContext>().Current = s.Get<BackgroundAuditContext>().Current with { TriggeredBy = "System" };
        await s.Db.SaveChangesAsync();
        Assert.Single(await s.Db.AuditTrails.Where(x => x.PkId == entity.Id).ToListAsync());
    }
    [Fact]
    public async Task Failed_business_write_has_no_success_history()
    {
        await using var s = fixture.Open();
        var product = NewProduct(); product.Price = -1; s.Db.Add(product);
        await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        Assert.False(await s.Db.AuditTrails.AnyAsync(x => x.PkId == product.Id));
        Assert.False(await s.Db.Products.AsNoTracking().AnyAsync(x => x.Id == product.Id));
    }
    [Fact]
    public async Task Rowversion_conflict_rolls_back_audit_history()
    {
        await using var one = fixture.Open(); var tenant = one.Tenant;
        var entity = new Category { Name = "Original" }; one.Db.Add(entity); await one.Db.SaveChangesAsync();
        await using var two = fixture.Open(tenant);
        var other = await two.Db.Categories.SingleAsync(x => x.Id == entity.Id);
        entity.Name = "Winner"; await one.Db.SaveChangesAsync();
        other.Name = "Loser";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => two.Db.SaveChangesAsync());
        Assert.Equal(1, await one.Db.AuditTrails.CountAsync(x => x.PkId == entity.Id && x.OperationType == "Update"));
    }
    [Fact]
    public async Task Detached_writes_and_synchronous_saves_are_explicitly_rejected()
    {
        await using var s = fixture.Open();
        s.Db.Update(new Category { Name = "Detached" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Db.SaveChangesAsync());
        Assert.Throws<NotSupportedException>(() => s.Db.SaveChanges());
    }
    [Fact]
    public async Task False_acceptance_requires_explicit_accept_before_another_save()
    {
        await using var s = fixture.Open(); var entity = new Category { Name = "False acceptance" };
        s.Db.Add(entity); await s.Db.SaveChangesAsync(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Db.SaveChangesAsync(false));
        s.Db.ChangeTracker.AcceptAllChanges(); Assert.Equal(0, await s.Db.SaveChangesAsync());
        Assert.Single(await s.Db.AuditTrails.Where(x => x.PkId == entity.Id).ToListAsync());
    }
    [Fact]
    public async Task Explicit_operation_id_replays_committed_insert_without_duplicate_history()
    {
        var info = new AuditContextInfo(Source: "Scheduler", TenantId: Guid.NewGuid().ToString("N"), OperationId: Guid.NewGuid());
        var id = Guid.NewGuid();
        await using (var first = fixture.Open(context: info))
        { first.Db.Add(new Category { Id = id, Name = "Idempotent" }); await first.Db.SaveChangesAsync(); }
        await using var retry = fixture.Open(context: info);
        retry.Db.Add(new Category { Id = id, Name = "Idempotent" });
        await retry.Db.SaveChangesAsync();
        Assert.Single(await retry.Db.AuditTrails.Where(x => x.PkId == id).ToListAsync());
    }
    [Fact]
    public async Task Audit_rows_cannot_be_modified_or_deleted()
    {
        await using var s = fixture.Open(); var entity = new Category { Name = "Immutable" };
        s.Db.Add(entity); await s.Db.SaveChangesAsync();
        var audit = await s.Db.AuditTrails.SingleAsync(x => x.PkId == entity.Id);
        audit.Description = "tampering";
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Db.SaveChangesAsync());
    }
    [Fact]
    public async Task Empty_keys_and_cancellation_are_preserved()
    {
        await using var s = fixture.Open(); s.Db.Add(new Category { Id = Guid.Empty, Name = "Empty" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Db.SaveChangesAsync());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Db.SaveChangesAsync(cancelled.Token));
    }
    [Fact]
    public async Task Precision_loss_and_detach_reattach_without_originals_are_rejected()
    {
        await using var s = fixture.Open(); var product = NewProduct(); product.Price = 1.001m; s.Db.Add(product);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Db.SaveChangesAsync());
        product.Price = 1m; await s.Db.SaveChangesAsync();
        s.Db.Entry(product).State = EntityState.Detached;
        s.Db.Update(product);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Db.SaveChangesAsync());
    }
    internal static Product NewProduct() => new() { Name = "Product", Price = 10, StockQuantity = 2,
        Category = new() { Name = "Category" }, Supplier = new() { Name = "Supplier", Email = "supplier@example.com" } };
}
