using AuditTrail.Domain.Common;

namespace AuditTrail.Application.Auditing;

public sealed record AuditContextInfo(string TriggeredBy = "System", string? UserId = null,
    string Source = "BackgroundWorker", string? CorrelationId = null, string? TenantId = null,
    string? Reason = null, Guid? OperationId = null);
public interface IAuditContextProvider { AuditContextInfo GetCurrent(); }
public interface IAuditAccessProvider { bool CanReadHistory { get; } string? TenantId { get; } }

// Set explicitly inside a trusted worker/Function message scope. Never populate from arbitrary headers.
public sealed class BackgroundAuditContext : IAuditContextProvider
{
    public AuditContextInfo Current { get; set; } = new();
    public AuditContextInfo GetCurrent() => Current;
}

public sealed record AuditEvent
{
    public required string EntityName { get; init; }
    public Guid? PkId { get; init; }
    public required string EventName { get; init; }
    public string? Description { get; init; }
    public string? Reason { get; init; }
    public string? ColumnName { get; init; }
    public object? OldValue { get; init; }
    public object? NewValue { get; init; }
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
    public Guid? OperationId { get; init; }
    public string? CorrelationId { get; init; }
}
public interface IAuditTrailWriter
{
    void AddManual(AuditEvent auditEvent);
    Task WriteStandaloneAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}

// Supported SQL: explicit IDs, optional equality conditions, and constant assignments only.
// Optional expected rowversions require every requested row to match or the whole command rolls back.
public enum BulkValue { UtcNow }
public sealed record AuditedBulkUpdateSpec<T>(IReadOnlyList<Guid> Ids,
    IReadOnlyDictionary<string, object?> Changes,
    IReadOnlyDictionary<string, object?>? Conditions = null,
    IReadOnlyDictionary<Guid, byte[]>? ExpectedVersions = null, Guid? OperationId = null)
    where T : class, IAuditableEntity;
public sealed record AuditedBulkDeleteSpec<T>(IReadOnlyList<Guid> Ids,
    IReadOnlyDictionary<string, object?>? Conditions = null,
    IReadOnlyDictionary<Guid, byte[]>? ExpectedVersions = null, Guid? OperationId = null)
    where T : class, IAuditableEntity;
public interface IAuditedBulkOperations
{
    Task<int> UpdateAsync<T>(AuditedBulkUpdateSpec<T> spec, CancellationToken ct = default) where T : class, IAuditableEntity;
    Task<int> DeleteAsync<T>(AuditedBulkDeleteSpec<T> spec, CancellationToken ct = default) where T : class, IAuditableEntity;
}

public sealed record AuditTrailQuery
{
    public string? EntityName { get; init; }
    public Guid? PkId { get; init; }
    public string? UserId { get; init; }
    public string? OperationType { get; init; }
    public string? AuditRecordType { get; init; }
    public string? EventName { get; init; }
    public Guid? OperationId { get; init; }
    public string? CorrelationId { get; init; }
    public string? TenantId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}
public sealed record AuditTrailDto(Guid Id, Guid? PkId, string EntityName, string? ColumnName,
    string? OldValue, string? NewValue, string? DataType, DateTimeOffset Timestamp, string TriggeredBy,
    string? UserId, string AuditRecordType, string OperationType, Guid OperationId, string? CorrelationId,
    string? Source, string? EventName, string? Reason, string? Description, string? TenantId,
    string? Metadata, short AuditSchemaVersion);
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public sealed record AuditOperationGroup(Guid OperationId, IReadOnlyList<AuditTrailDto> Records);
public interface IAuditTrailQueryService
{
    Task<PagedResult<AuditTrailDto>> SearchAsync(AuditTrailQuery query, CancellationToken ct = default);
    Task<PagedResult<AuditOperationGroup>> GroupedAsync(AuditTrailQuery query, CancellationToken ct = default);
}
