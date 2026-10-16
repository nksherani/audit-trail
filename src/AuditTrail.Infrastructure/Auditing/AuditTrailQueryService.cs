using System.Diagnostics;
using AuditTrail.Application.Auditing;
using AuditTrail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuditTrail.Infrastructure.Auditing;

public sealed class AuditTrailQueryService(AppDbContext db, IAuditAccessProvider access, ILogger<AuditTrailQueryService> logger) : IAuditTrailQueryService
{
    private IQueryable<AuditRecord> Filter(AuditTrailQuery q)
    {
        if (!access.CanReadHistory || access.TenantId != db.CurrentTenantId || (q.TenantId is not null && q.TenantId != access.TenantId))
            throw new UnauthorizedAccessException("Audit history access is denied for this tenant.");
        if (q.Page < 1 || q.PageSize is < 1 or > 200 || (q.From > q.To)) throw new ArgumentException("Invalid pagination or time range.");
        var query = db.AuditTrails.AsNoTracking().Where(x => x.TenantId == access.TenantId);
        if (q.EntityName is not null) query = query.Where(x => x.EntityName == q.EntityName);
        if (q.PkId is not null) query = query.Where(x => x.PkId == q.PkId);
        if (q.UserId is not null) query = query.Where(x => x.UserId == q.UserId);
        if (q.OperationType is not null) query = query.Where(x => x.OperationType == q.OperationType);
        if (q.AuditRecordType is not null) query = query.Where(x => x.AuditRecordType == q.AuditRecordType);
        if (q.EventName is not null) query = query.Where(x => x.EventName == q.EventName);
        if (q.OperationId is not null) query = query.Where(x => x.OperationId == q.OperationId);
        if (q.CorrelationId is not null) query = query.Where(x => x.CorrelationId == q.CorrelationId);
        if (q.From is not null) query = query.Where(x => x.Timestamp >= q.From);
        if (q.To is not null) query = query.Where(x => x.Timestamp <= q.To);
        return query;
    }
    public async Task<PagedResult<AuditTrailDto>> SearchAsync(AuditTrailQuery query, CancellationToken ct = default)
    {
        var timer = Stopwatch.StartNew();
        var filtered = Filter(query);
        var total = await filtered.CountAsync(ct);
        var rows = await filtered.OrderBy(x => x.Timestamp).ThenBy(x => x.Id)
            .Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize).ToListAsync(ct);
        AuditDiagnostics.QueryMilliseconds.Record(timer.Elapsed.TotalMilliseconds);
        if (timer.Elapsed.TotalSeconds > 1) logger.LogWarning("Slow audit query: {Milliseconds} ms", timer.Elapsed.TotalMilliseconds);
        return new(rows.Select(Map).ToList(), total, query.Page, query.PageSize);
    }
    public async Task<PagedResult<AuditOperationGroup>> GroupedAsync(AuditTrailQuery query, CancellationToken ct = default)
    {
        var filtered = Filter(query);
        var groups = filtered.GroupBy(x => x.OperationId).Select(x => new { OperationId = x.Key, Timestamp = x.Min(r => r.Timestamp) });
        var total = await groups.CountAsync(ct);
        var ids = await groups.OrderBy(x => x.Timestamp).ThenBy(x => x.OperationId)
            .Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize).Select(x => x.OperationId).ToListAsync(ct);
        // Page operations, never individual fields, so a group is not split across pages.
        var rows = await filtered.Where(x => ids.Contains(x.OperationId)).OrderBy(x => x.Timestamp).ThenBy(x => x.Id).ToListAsync(ct);
        return new(ids.Select(id => new AuditOperationGroup(id, rows.Where(x => x.OperationId == id).Select(Map).ToList())).ToList(), total, query.Page, query.PageSize);
    }
    public static AuditTrailDto Map(AuditRecord x) => new(x.Id, x.PkId, x.EntityName, x.ColumnName, x.OldValue, x.NewValue,
        x.DataType, x.Timestamp, x.TriggeredBy, x.UserId, x.AuditRecordType, x.OperationType, x.OperationId,
        x.CorrelationId, x.Source, x.EventName, x.Reason, x.Description, x.TenantId, x.Metadata, x.AuditSchemaVersion);
}
