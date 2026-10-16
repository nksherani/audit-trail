using System.Diagnostics;
using AuditTrail.Application.Auditing;
using AuditTrail.Domain.Entities;
using AuditTrail.Infrastructure.Auditing;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace AuditTrail.Tests;

[Collection("SQL Server")]
public sealed class PerformanceTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    [Fact, Trait("Category", "Performance")]
    public async Task Thousand_row_baseline_audited_insert_bulk_and_large_rollback()
    {
        const int count = 1000;
        var tenant = Guid.NewGuid().ToString("N");
        var baselineOptions = new DbContextOptionsBuilder<BaselineContext>().UseSqlServer(fixture.ConnectionString).Options;
        await using var baseline = new BaselineContext(baselineOptions);
        baseline.Add(new Category { Name = "Warm baseline", TenantId = tenant }); await baseline.SaveChangesAsync();
        var timer = Stopwatch.StartNew();
        baseline.AddRange(Enumerable.Range(0, count).Select(i => new Category { Name = $"Baseline {i}", TenantId = tenant }));
        await baseline.SaveChangesAsync();
        var baselineMs = timer.Elapsed.TotalMilliseconds;
        await using var audited = fixture.Open(tenant);
        audited.Db.Add(new Category { Name = "Warm audit" }); await audited.Db.SaveChangesAsync();
        var entities = Enumerable.Range(0, count).Select(i => new Category { Name = $"Audited {i}" }).ToArray();
        timer.Restart(); audited.Db.AddRange(entities); await audited.Db.SaveChangesAsync();
        var auditedMs = timer.Elapsed.TotalMilliseconds;
        var ids = entities.Select(x => x.Id).ToArray();
        Assert.Equal(count, await audited.Db.AuditTrails.CountAsync(x => x.OperationType == "Insert" && ids.Contains(x.PkId!.Value)));
        await using var bulkScope = fixture.Open(tenant);
        timer.Restart();
        Assert.Equal(count, await bulkScope.Get<IAuditedBulkOperations>().UpdateAsync(new AuditedBulkUpdateSpec<Category>(ids,
            new Dictionary<string, object?> { ["Description"] = "Bulk updated" })));
        var bulkMs = timer.Elapsed.TotalMilliseconds;
        await using var failing = fixture.Open(context: new("InvalidActor", Source: "PerformanceRollback", TenantId: tenant));
        await Assert.ThrowsAnyAsync<Exception>(() => failing.Get<IAuditedBulkOperations>().UpdateAsync(new AuditedBulkUpdateSpec<Category>(ids,
            new Dictionary<string, object?> { ["Name"] = "Must roll back" })));
        Assert.False(await bulkScope.Db.Categories.AsNoTracking().AnyAsync(x => ids.Contains(x.Id) && x.Name == "Must roll back"));
        timer.Restart();
        var page = await bulkScope.Get<IAuditTrailQueryService>().SearchAsync(new() { OperationType = "Update", PageSize = 50 });
        var queryMs = timer.Elapsed.TotalMilliseconds;
        Assert.Equal(count, page.TotalCount);
        var bytes = await bulkScope.Db.AuditTrails.Where(x => x.TenantId == tenant).SumAsync(x =>
            (long)((x.OldValue ?? "").Length + (x.NewValue ?? "").Length) * 2);
        output.WriteLine($"PERFORMANCE rows={count}; baselineInsertMs={baselineMs:F1}; auditedInsertMs={auditedMs:F1}; bulkUpdateMs={bulkMs:F1}; historyPageMs={queryMs:F1}; valuePayloadBytes={bytes}");
        // No invented timing SLO: record a measured baseline and assert complete coverage/rollback.
    }
    private sealed class BaselineContext(DbContextOptions<BaselineContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Category>(e =>
            {
                e.ToTable("Categories"); e.Ignore(x => x.Products);
                e.Property(x => x.Id).ValueGeneratedNever(); e.Property(x => x.RowVersion).IsRowVersion();
                e.Property(x => x.TenantId).HasMaxLength(100); e.Property(x => x.Name).HasMaxLength(100);
                e.Property(x => x.Description).HasMaxLength(500);
            });
        }
    }
}
