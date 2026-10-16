using System.Data.Common;
using AuditTrail.Application.Auditing;
using AuditTrail.Domain.Entities;
using AuditTrail.Infrastructure.Auditing;
using AuditTrail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuditTrail.Tests;

[Collection("SQL Server")]
public sealed class ResilienceTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Transient_command_failure_reuses_staged_audit_ids()
    {
        await using var scope = fixture.Open();
        var failure = new TransientCommandFailure();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(fixture.ConnectionString, sql => sql.EnableRetryOnFailure(3))
            .AddInterceptors(failure).Options;
        await using var db = new AppDbContext(options, scope.Get<IAuditContextProvider>(), scope.Get<AuditTrailBuilder>(), TimeProvider.System, NullLogger<AppDbContext>.Instance);
        var entity = new Category { Name = "Transient retry" }; db.Add(entity); await db.SaveChangesAsync();
        Assert.True(failure.Triggered);
        Assert.Single(await db.AuditTrails.Where(x => x.PkId == entity.Id).ToListAsync());
    }
    [Fact]
    public async Task Lost_commit_acknowledgement_is_verified_using_atomic_operation_receipt()
    {
        await using var scope = fixture.Open();
        var failure = new LostCommitAcknowledgement();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(fixture.ConnectionString).AddInterceptors(failure).Options;
        await using var db = new AppDbContext(options, scope.Get<IAuditContextProvider>(), scope.Get<AuditTrailBuilder>(), TimeProvider.System, NullLogger<AppDbContext>.Instance);
        var entity = new Category { Name = "Commit acknowledged by receipt" }; db.Add(entity);
        await db.SaveChangesAsync();
        Assert.True(failure.Triggered);
        Assert.Single(await db.AuditTrails.Where(x => x.PkId == entity.Id).ToListAsync());
        Assert.Equal(EntityState.Unchanged, db.Entry(entity).State);
    }
    private sealed class TransientCommandFailure : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (!Triggered && command.CommandText.Contains("INSERT INTO [Categories]", StringComparison.Ordinal))
            { Triggered = true; throw new TimeoutException("Injected transient command timeout."); }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class LostCommitAcknowledgement : DbTransactionInterceptor
    {
        public bool Triggered { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
        {
            if (!Triggered) { Triggered = true; throw new IOException("Injected lost commit acknowledgement."); }
            return Task.CompletedTask;
        }
    }
}
