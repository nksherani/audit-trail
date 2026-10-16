using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuditTrail.Application.Auditing;
using AuditTrail.Domain.Common;
using AuditTrail.Domain.Entities;
using AuditTrail.Infrastructure.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;

namespace AuditTrail.Infrastructure.Persistence;

public partial class AppDbContext
{
    private readonly IAuditContextProvider _contextProvider;
    private readonly AuditTrailBuilder _builder;
    private readonly TimeProvider _clock;
    private readonly ILogger<AppDbContext> _logger;
    private readonly HashSet<object> _trusted = new(ReferenceEqualityComparer.Instance);
    private readonly List<AuditEvent> _manual = [];
    private Guid? _pendingOperationId;
    private bool _awaitingAcceptance;
    public string? CurrentTenantId { get; }
    public DbSet<AuditRecord> AuditTrails => Set<AuditRecord>();
    internal DbSet<AuditOperation> AuditOperations => Set<AuditOperation>();

    public AppDbContext(DbContextOptions<AppDbContext> options, IAuditContextProvider contextProvider,
        AuditTrailBuilder builder, TimeProvider clock, ILogger<AppDbContext> logger) : base(options)
    {
        _contextProvider = contextProvider; _builder = builder; _clock = clock; _logger = logger;
        CurrentTenantId = contextProvider.GetCurrent().TenantId;
        ChangeTracker.Tracked += (_, args) => { if (args.FromQuery) _trusted.Add(args.Entry.Entity); };
        ChangeTracker.StateChanged += (_, args) => { if (args.NewState == EntityState.Detached) _trusted.Remove(args.Entry.Entity); };
    }
    internal void Stage(AuditEvent auditEvent) => _manual.Add(auditEvent);
    public override int SaveChanges() => throw new NotSupportedException("Use SaveChangesAsync; synchronous writes are prohibited to prevent audit bypass.");
    public override int SaveChanges(bool acceptAllChangesOnSuccess) => SaveChanges();
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => SaveChangesAsync(true, cancellationToken);

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Database.AutoTransactionBehavior == AutoTransactionBehavior.Never)
            throw new InvalidOperationException("Mandatory auditing requires transactional SaveChanges.");
        var context = _contextProvider.GetCurrent();
        if (context.TenantId != CurrentTenantId) throw new InvalidOperationException("Tenant context cannot change during a unit of work.");
        ChangeTracker.DetectChanges();
        var pending = ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        if (_awaitingAcceptance)
        {
            if (pending.Count > 0) throw new InvalidOperationException("The previous save used acceptAllChangesOnSuccess=false. Call ChangeTracker.AcceptAllChanges before saving again.");
            _awaitingAcceptance = false;
        }
        if (pending.Any(x => x.Entity is AuditRecord or AuditOperation))
            throw new InvalidOperationException("Audit history is append-only and can only be written through the audit framework.");
        var entries = pending.Where(e => e.Entity is IAuditableEntity).ToList();
        foreach (var entry in entries)
        {
            if (entry.State != EntityState.Added && !_trusted.Contains(entry.Entity))
                throw new InvalidOperationException("Detached update/delete is unsupported. Load a tracked entity before changing it.");
            if (entry.Entity is ITenantEntity tenant)
            {
                if (entry.State == EntityState.Added && tenant.TenantId is null) tenant.TenantId = CurrentTenantId;
                if (tenant.TenantId != CurrentTenantId || (entry.State != EntityState.Added && entry.Property(nameof(ITenantEntity.TenantId)).IsModified))
                    throw new UnauthorizedAccessException("Cross-tenant writes and tenant reassignment are prohibited.");
            }
        }
        await ValidateProductReferencesAsync(entries, cancellationToken);
        ChangeTracker.DetectChanges();
        var manualOperationId = _manual.Where(x => x.OperationId.HasValue).Select(x => x.OperationId).Distinct().SingleOrDefault();
        var operationId = _pendingOperationId ?? context.OperationId ?? manualOperationId ?? Guid.NewGuid();
        _pendingOperationId = operationId;
        if (operationId == Guid.Empty) throw new ArgumentException("OperationId must not be empty.");
        var timestamp = _clock.GetUtcNow();
        var records = entries.SelectMany(x => _builder.Build(x, operationId, context, timestamp)).ToList();
        foreach (var auditEvent in _manual)
        {
            await ValidateManualTenantAsync(auditEvent, entries, cancellationToken);
            records.Add(_builder.Manual(auditEvent, Model, operationId, context, timestamp));
        }
        if (records.Count == 0 && pending.Count == 0) { _pendingOperationId = null; return 0; }
        // A receipt also protects saves whose only changes are excluded fields.
        var fingerprint = Fingerprint(pending.Select(x => new
        {
            Entity = x.Metadata.ClrType.FullName, State = x.State.ToString(),
            Values = x.Properties.Where(p => p.Metadata.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never)
                .OrderBy(p => p.Metadata.Name).ToDictionary(p => p.Metadata.Name, p => p.CurrentValue)
        }).OrderBy(x => x.Entity).ThenBy(x => JsonSerializer.Serialize(x.Values)).ToArray(), _manual);
        var existing = await AuditOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);
        if (existing is not null) return await CompleteReplayAsync(existing, fingerprint, acceptAllChangesOnSuccess, cancellationToken);
        var receipt = new AuditOperation { Id = operationId, TenantId = CurrentTenantId, Fingerprint = fingerprint,
            Timestamp = timestamp, ResultCount = pending.Count + records.Count + 1 };
        AuditTrails.AddRange(records);
        AuditOperations.Add(receipt);
        try
        {
            // One underlying save: business rows, auto/manual audit rows, and the operation receipt.
            var count = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            Finish(acceptAllChangesOnSuccess, entries);
            _logger.LogInformation("Audit operation {OperationId} persisted {AuditCount} audit records", operationId, records.Count);
            AuditDiagnostics.Records.Add(records.Count);
            return count;
        }
        catch (Exception exception)
        {
            foreach (var record in records) Entry(record).State = EntityState.Detached;
            Entry(receipt).State = EntityState.Detached;
            AuditDiagnostics.Failures.Add(1);
            _logger.LogWarning("Audit save {OperationId} failed ({ExceptionType}); generated entries detached for retry", operationId, exception.GetType().Name);
            // Cancellation must retain normal cancellation semantics; a later retry can verify the receipt.
            if (exception is not OperationCanceledException && Database.CurrentTransaction is null)
            {
                try
                {
                    var committed = await AuditOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);
                    if (committed is not null) return await CompleteReplayAsync(committed, fingerprint, acceptAllChangesOnSuccess, cancellationToken);
                }
                catch (Exception verification) when (verification is not OperationCanceledException && verification is not InvalidOperationException)
                { _logger.LogWarning("Commit verification unavailable for {OperationId}; retry with the same operation ID", operationId); }
            }
            throw;
        }
    }
    private async Task<int> CompleteReplayAsync(AuditOperation receipt, string fingerprint, bool accept, CancellationToken ct)
    {
        if (receipt.Kind != "Save" || receipt.TenantId != CurrentTenantId || receipt.Fingerprint != fingerprint)
            throw new InvalidOperationException("OperationId was reused for a different command or tenant.");
        var entries = ChangeTracker.Entries().Where(x => x.Entity is IAuditableEntity).ToList();
        // A fresh context replaying a committed insert must also recover database rowversions.
        foreach (var entry in entries.Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            var stored = await entry.GetDatabaseValuesAsync(ct)
                ?? throw new InvalidOperationException("Committed operation exists, but its entity is no longer available. Reload the command result.");
            foreach (var property in entry.Properties.Where(x => x.Metadata.ValueGenerated != Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never))
                property.CurrentValue = stored[property.Metadata.Name];
        }
        if (accept) ChangeTracker.AcceptAllChanges();
        Finish(accept, entries);
        return receipt.ResultCount;
    }
    private void Finish(bool accept, List<EntityEntry> entries)
    {
        foreach (var entry in entries) _trusted.Add(entry.Entity);
        _manual.Clear(); _pendingOperationId = null; _awaitingAcceptance = !accept;
    }
    internal static string Fingerprint(params object?[] values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));

    private async Task ValidateProductReferencesAsync(List<EntityEntry> entries, CancellationToken ct)
    {
        var products = entries.Where(x => x.Entity is Product && (x.State == EntityState.Added ||
            (x.State == EntityState.Modified && (x.Property(nameof(Product.CategoryId)).IsModified || x.Property(nameof(Product.SupplierId)).IsModified))))
            .Select(x => (Product)x.Entity).ToList();
        if (products.Count == 0) return;
        var categories = products.Select(x => x.CategoryId).Distinct().Except(entries.Where(x => x.Entity is Category && x.State == EntityState.Added).Select(x => ((Category)x.Entity).Id)).ToArray();
        var suppliers = products.Select(x => x.SupplierId).Distinct().Except(entries.Where(x => x.Entity is Supplier && x.State == EntityState.Added).Select(x => ((Supplier)x.Entity).Id)).ToArray();
        if (await Categories.CountAsync(x => categories.Contains(x.Id), ct) != categories.Length ||
            await Suppliers.CountAsync(x => suppliers.Contains(x.Id), ct) != suppliers.Length)
            throw new InvalidOperationException("Product references must exist in the current tenant.");
    }
    private async Task ValidateManualTenantAsync(AuditEvent auditEvent, List<EntityEntry> entries, CancellationToken ct)
    {
        if (auditEvent.PkId is not Guid id) return;
        var entity = Model.GetEntityTypes().SingleOrDefault(x => x.ClrType.FullName == auditEvent.EntityName);
        if (entity is null || !typeof(ITenantEntity).IsAssignableFrom(entity.ClrType))
            throw new InvalidOperationException("Entity-bound manual events must identify a mapped tenant entity. Use a standalone event without PkId otherwise.");
        if (entries.Any(x => x.Metadata == entity && Equals(x.Property("Id").CurrentValue, id))) return;
        var method = typeof(AppDbContext).GetMethod(nameof(EntityExistsAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var exists = await (Task<bool>)method.MakeGenericMethod(entity.ClrType).Invoke(this, [id, ct])!;
        if (!exists) throw new UnauthorizedAccessException("Manual event entity is unavailable in the current tenant.");
    }
    private Task<bool> EntityExistsAsync<T>(Guid id, CancellationToken ct) where T : class =>
        Set<T>().AnyAsync(x => EF.Property<Guid>(x, "Id") == id, ct);
}
