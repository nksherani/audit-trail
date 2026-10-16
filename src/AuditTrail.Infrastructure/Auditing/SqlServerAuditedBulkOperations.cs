using System.Data;
using System.Diagnostics;
using System.Text.Json;
using AuditTrail.Application.Auditing;
using AuditTrail.Domain.Common;
using AuditTrail.Domain.Entities;
using AuditTrail.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace AuditTrail.Infrastructure.Auditing;

public sealed class SqlServerAuditedBulkOperations(IDbContextFactory<AppDbContext> factory, AppDbContext scopeDb,
    IAuditContextProvider contextProvider, AuditPropertyPolicy policy, AuditTrailBuilder builder,
    AuditOptions options, TimeProvider clock, ILogger<SqlServerAuditedBulkOperations> logger) : IAuditedBulkOperations
{
    public Task<int> UpdateAsync<T>(AuditedBulkUpdateSpec<T> spec, CancellationToken ct = default) where T : class, IAuditableEntity =>
        ExecuteAsync<T>(spec.Ids, spec.Changes, spec.Conditions, spec.ExpectedVersions, spec.OperationId, ct);
    public Task<int> DeleteAsync<T>(AuditedBulkDeleteSpec<T> spec, CancellationToken ct = default) where T : class, IAuditableEntity =>
        ExecuteAsync<T>(spec.Ids, null, spec.Conditions, spec.ExpectedVersions, spec.OperationId, ct);

    private async Task<int> ExecuteAsync<T>(IReadOnlyList<Guid> ids, IReadOnlyDictionary<string, object?>? changes,
        IReadOnlyDictionary<string, object?>? conditions, IReadOnlyDictionary<Guid, byte[]>? versions, Guid? requestedOperationId, CancellationToken ct)
        where T : class, IAuditableEntity
    {
        ct.ThrowIfCancellationRequested();
        if (System.Transactions.Transaction.Current is not null || scopeDb.Database.CurrentTransaction is not null ||
            scopeDb.ChangeTracker.Entries<IAuditableEntity>().Any())
            throw new InvalidOperationException("Bulk commands require a dedicated scope with no tracked business entities or caller-managed transaction.");
        if (ids.Count == 0 || ids.Count > options.MaxBulkRows || ids.Any(x => x == Guid.Empty) || ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("Bulk IDs must be unique, nonempty GUIDs within the configured row limit.");
        if (changes is { Count: 0 } || changes?.Count > 20 || conditions?.Count > 20)
            throw new ArgumentException("Bulk specifications require 1–20 assignments and at most 20 equality conditions.");
        if (versions is not null && (ids.Count > 500 || versions.Count != ids.Count || ids.Any(x => !versions.ContainsKey(x)) || versions.Values.Any(x => x.Length != 8)))
            throw new ArgumentException("Expected versions must cover every requested ID (maximum 500 rows, 8 bytes per rowversion).");
        var context = contextProvider.GetCurrent();
        var operationId = requestedOperationId ?? context.OperationId ?? Guid.NewGuid();
        if (operationId == Guid.Empty) throw new ArgumentException("OperationId cannot be empty.");
        var kind = changes is null ? "BulkDelete" : "BulkUpdate";
        var fingerprint = AppDbContext.Fingerprint(typeof(T).FullName, kind, ids.Order().ToArray(),
            changes?.OrderBy(x => x.Key).ToArray(), conditions?.OrderBy(x => x.Key).ToArray(), versions?.OrderBy(x => x.Key).ToArray());
        var timestamp = clock.GetUtcNow();
        var timer = Stopwatch.StartNew();
        await using var coordinator = await factory.CreateDbContextAsync(ct);
        var strategy = coordinator.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                var prior = await db.Set<AuditOperation>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, ct);
                if (prior is not null)
                {
                    if (prior.TenantId != context.TenantId || prior.Kind != kind || prior.Fingerprint != fingerprint)
                        throw new InvalidOperationException("OperationId is already associated with a different command or tenant.");
                    return prior.ResultCount;
                }
                var entity = db.Model.FindEntityType(typeof(T)) ?? throw new NotSupportedException("Unmapped bulk entity.");
                if (!typeof(ITenantEntity).IsAssignableFrom(typeof(T)) || entity.IsOwned() || entity.GetComplexProperties().Any())
                    throw new NotSupportedException("Bulk operations require a mapped tenant entity with scalar properties.");
                var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
                string Column(IProperty p) => Quote(p.GetColumnName(table)!);
                var key = entity.FindPrimaryKey()?.Properties;
                if (key?.Count != 1 || key[0].ClrType != typeof(Guid) || key[0].ValueGenerated != ValueGenerated.Never)
                    throw new NotSupportedException("Bulk operations require an application-generated GUID key.");
                IProperty Property(string name, bool assignment)
                {
                    var p = entity.FindProperty(name) ?? throw new ArgumentException($"Unknown bulk property: {name}.");
                    if (p.IsShadowProperty() || (assignment && (p.IsPrimaryKey() || p.IsConcurrencyToken || p.ValueGenerated != ValueGenerated.Never || name == "TenantId")))
                        throw new NotSupportedException($"Bulk assignment/filter is unsupported for {name}.");
                    return p;
                }
                var assignments = changes?.Select(x => (Property: Property(x.Key, true), x.Value)).ToArray();
                var eligible = entity.GetProperties().Where(x => policy.Resolve(x) != AuditDisposition.Ignore).ToArray();
                var captured = changes is null ? eligible : assignments!.Select(x => x.Property)
                    .Where(x => policy.Resolve(x) != AuditDisposition.Ignore).Prepend(key[0]).Distinct().ToArray();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var connection = (SqlConnection)db.Database.GetDbConnection();
                var sqlTransaction = (SqlTransaction)transaction.GetDbTransaction();
                await using var command = connection.CreateCommand();
                command.Transaction = sqlTransaction;
                command.CommandTimeout = 60;
                int parameterIndex = 0;
                string Parameter(object? value, IProperty? p = null)
                {
                    if (value is BulkValue.UtcNow)
                    {
                        var type = p is null ? null : Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
                        if (type != typeof(DateTimeOffset) && type != typeof(DateTime))
                            throw new ArgumentException("BulkValue.UtcNow requires a date/time property.");
                        value = type == typeof(DateTime) ? timestamp.UtcDateTime : (object)timestamp;
                    }
                    if (value is JsonElement json && p is not null) value = JsonSerializer.Deserialize(json.GetRawText(), p.ClrType);
                    if (value is not null && p is not null && !(Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType).IsInstanceOfType(value))
                        throw new ArgumentException($"Bulk value for {p.Name} must be {p.ClrType.Name}.");
                    if (value is null && p is { IsNullable: false }) throw new ArgumentException($"{p.Name} cannot be null.");
                    var name = $"@p{parameterIndex++}";
                    var providerValue = p?.GetTypeMapping().Converter?.ConvertToProvider(value) ?? value;
                    var parameter = command.Parameters.AddWithValue(name, providerValue ?? DBNull.Value);
                    if (p?.GetPrecision() is int precision) parameter.Precision = (byte)precision;
                    if (p?.GetScale() is int scale) parameter.Scale = (byte)scale;
                    return name;
                }
                // IDs use a single JSON parameter, avoiding SQL Server's 2100-parameter limit.
                var where = $"{Column(key[0])} IN (SELECT CONVERT(uniqueidentifier,[value]) FROM OPENJSON({Parameter(JsonSerializer.Serialize(ids))}))";
                where += context.TenantId is null ? " AND [TenantId] IS NULL" : $" AND [TenantId] = {Parameter(context.TenantId)}";
                foreach (var condition in conditions ?? new Dictionary<string, object?>())
                {
                    var p = Property(condition.Key, false);
                    where += condition.Value is null ? $" AND {Column(p)} IS NULL" : $" AND {Column(p)} = {Parameter(condition.Value, p)}";
                }
                if (versions is not null)
                    where += " AND (" + string.Join(" OR ", versions.Select(x => $"({Column(key[0])} = {Parameter(x.Key)} AND [RowVersion] = {Parameter(x.Value)})")) + ")";
                if (assignments is not null)
                {
                    // Relationship reassignment must not cross the trusted tenant boundary.
                    foreach (var assignment in assignments.Where(x => x.Property.IsForeignKey()))
                    {
                        if (typeof(T) != typeof(Product) || assignment.Value is not Guid reference)
                            throw new NotSupportedException("Unsupported bulk foreign-key assignment.");
                        var valid = assignment.Property.Name == "CategoryId"
                            ? await db.Categories.AnyAsync(x => x.Id == reference, ct)
                            : await db.Suppliers.AnyAsync(x => x.Id == reference, ct);
                        if (!valid) throw new UnauthorizedAccessException("Bulk references must exist in the current tenant.");
                    }
                }
                var output = string.Join(",", captured.Select(p => $"DELETED.{Column(p)}"));
                if (assignments is not null) output += "," + string.Join(",", captured.Select(p => $"INSERTED.{Column(p)}"));
                var qualifiedTable = $"{Quote(entity.GetSchema() ?? "dbo")}.{Quote(entity.GetTableName()!)}";
                command.CommandText = assignments is null
                    ? $"DELETE FROM {qualifiedTable} OUTPUT {output} WHERE {where};"
                    : $"UPDATE {qualifiedTable} SET {string.Join(",", assignments.Select(x => $"{Column(x.Property)} = {Parameter(x.Value, x.Property)}"))} OUTPUT {output} WHERE {where};";
                var records = new List<AuditRecord>();
                var affected = 0;
                await using (var reader = await command.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        if (++affected > options.MaxBulkRows) throw new InvalidOperationException("Bulk capture exceeded the configured limit.");
                        object? Value(int index, IProperty property)
                        {
                            var value = reader.IsDBNull(index) ? null : reader.GetValue(index);
                            return property.GetTypeMapping().Converter?.ConvertFromProvider(value) ?? value;
                        }
                        var before = captured.Select((p, i) => (p.Name, Value: Value(i, p))).ToDictionary(x => x.Name, x => x.Value);
                        var id = (Guid)before[key[0].Name]!;
                        if (assignments is null) records.Add(builder.Snapshot(entity, id, before, true, operationId, context, timestamp));
                        else foreach (var item in captured.Select((p, i) => (Property: p, Index: i)).Where(x => !x.Property.IsPrimaryKey()))
                        {
                            var after = Value(item.Index + captured.Length, item.Property);
                            if (!AuditValueSerializer.Equal(item.Property, before[item.Property.Name], after))
                                records.Add(builder.Change(entity, id, item.Property, before[item.Property.Name], after, operationId, context, timestamp));
                        }
                    }
                }
                if (versions is not null && affected != ids.Count) throw new DbUpdateConcurrencyException("A bulk rowversion condition failed or a row was unavailable.");
                await CopyAuditRecordsAsync(connection, sqlTransaction, records, ct);
                await using var receiptCommand = connection.CreateCommand();
                receiptCommand.Transaction = sqlTransaction;
                receiptCommand.CommandText = "INSERT INTO [AuditOperations] ([Id],[TenantId],[Kind],[Fingerprint],[ResultCount],[Timestamp]) VALUES (@id,@tenant,@kind,@fingerprint,@count,@time)";
                receiptCommand.Parameters.AddWithValue("@id", operationId);
                receiptCommand.Parameters.AddWithValue("@tenant", (object?)context.TenantId ?? DBNull.Value);
                receiptCommand.Parameters.AddWithValue("@kind", kind);
                receiptCommand.Parameters.AddWithValue("@fingerprint", fingerprint);
                receiptCommand.Parameters.AddWithValue("@count", affected);
                receiptCommand.Parameters.AddWithValue("@time", timestamp);
                await receiptCommand.ExecuteNonQueryAsync(ct);
                await transaction.CommitAsync(ct);
                AuditDiagnostics.Records.Add(records.Count);
                logger.LogInformation("{Kind} operation {OperationId}: {AffectedRows} rows, {AuditCount} audit entries", kind, operationId, affected, records.Count);
                return affected;
            });
        }
        catch (Exception ex)
        {
            AuditDiagnostics.Failures.Add(1);
            logger.LogWarning("Bulk operation {OperationId} failed ({ExceptionType}); retry using the same operation ID", operationId, ex.GetType().Name);
            throw;
        }
        finally { AuditDiagnostics.BulkMilliseconds.Record(timer.Elapsed.TotalMilliseconds); }
    }
    private async Task CopyAuditRecordsAsync(SqlConnection connection, SqlTransaction transaction, List<AuditRecord> records, CancellationToken ct)
    {
        var properties = typeof(AuditRecord).GetProperties();
        using var table = new DataTable();
        foreach (var p in properties) table.Columns.Add(p.Name, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType);
        using var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.KeepNulls, transaction)
        { DestinationTableName = "[dbo].[AuditTrail]", BatchSize = options.BulkCopyBatchSize, BulkCopyTimeout = 60 };
        foreach (var p in properties) copy.ColumnMappings.Add(p.Name, p.Name);
        foreach (var record in records)
        {
            table.Rows.Add(properties.Select(p => p.GetValue(record) ?? DBNull.Value).ToArray());
            if (table.Rows.Count >= options.BulkCopyBatchSize) { await copy.WriteToServerAsync(table, ct); table.Clear(); }
        }
        if (table.Rows.Count > 0) await copy.WriteToServerAsync(table, ct);
    }
    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
