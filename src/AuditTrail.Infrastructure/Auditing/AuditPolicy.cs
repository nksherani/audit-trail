using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuditTrail.Domain.Common;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AuditTrail.Infrastructure.Auditing;

public enum AuditDisposition { Include, Mask, Ignore }
public sealed class AuditOptions
{
    public HashSet<string> IgnoredNames { get; } = new(StringComparer.OrdinalIgnoreCase)
        { "Password", "PasswordHash", "AccessToken", "RefreshToken", "ApiKey", "Secret", "ConnectionString" };
    public HashSet<string> MaskedNames { get; } = new(StringComparer.OrdinalIgnoreCase) { "Email" };
    public HashSet<string> UnsupportedNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int MaxValueLength { get; set; } = 65536;
    public int MaxBulkRows { get; set; } = 10000;
    public int BulkCopyBatchSize { get; set; } = 1000;
}
public sealed class AuditPropertyPolicy(AuditOptions options)
{
    public AuditDisposition Resolve(IProperty property)
    {
        if (property.IsShadowProperty() || property.ClrType == typeof(byte[]) ||
            property.ValueGenerated != ValueGenerated.Never ||
            options.IgnoredNames.Contains(property.Name) || property.PropertyInfo?.IsDefined(typeof(AuditIgnoreAttribute)) == true)
            return AuditDisposition.Ignore;
        if (options.UnsupportedNames.Contains(property.Name)) throw new NotSupportedException($"Property {property.Name} is not supported for auditing.");
        return options.MaskedNames.Contains(property.Name) || property.PropertyInfo?.IsDefined(typeof(AuditMaskAttribute)) == true
            ? AuditDisposition.Mask : AuditDisposition.Include;
    }
}
public sealed class AuditValueSerializer(AuditOptions options)
{
    public const string Mask = "[REDACTED]";
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() }, MaxDepth = 16 };

    public string? Serialize(object? value, bool allowJson = false)
    {
        if (value is null) return null;
        var type = value.GetType();
        if (!(type.IsEnum || value is string or bool or Guid or short or int or long or byte or sbyte or ushort or uint or ulong
            or float or double or decimal or DateTime or DateTimeOffset or DateOnly or TimeOnly or JsonElement or JsonDocument) && !allowJson)
            throw new NotSupportedException($"Audit serialization of {type.FullName} requires an explicit JSON policy.");
        // Scalar values are JSON tokens too: null, empty strings, and literal "null" remain distinct.
        return Check(allowJson ? JsonSerializer.Serialize(Redact(JsonSerializer.SerializeToElement(value, type, Json)), Json)
            : JsonSerializer.Serialize(value, type, Json));
    }
    public object? SnapshotValue(IProperty property, object? value, AuditDisposition disposition)
    {
        if (disposition == AuditDisposition.Mask) return value is null ? null : Mask;
        var json = Serialize(value, property.PropertyInfo?.IsDefined(typeof(AuditJsonAttribute)) == true);
        return json is null ? null : JsonSerializer.Deserialize<JsonElement>(json);
    }
    public string Snapshot(SortedDictionary<string, object?> values) => Check(JsonSerializer.Serialize(values, Json));
    public string? Metadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null) return null;
        var element = JsonSerializer.SerializeToElement(metadata, Json);
        return Check(JsonSerializer.Serialize(Redact(element), Json));
    }
    private object? Redact(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(x => x.Name, x => options.IgnoredNames.Contains(x.Name) || options.MaskedNames.Contains(x.Name)
                ? (object?)Mask : Redact(x.Value)),
        JsonValueKind.Array => value.EnumerateArray().Select(Redact).ToArray(),
        _ => value.Clone()
    };
    public string Check(string value) => value.Length <= options.MaxValueLength ? value
        : throw new InvalidOperationException("Audit value exceeds configured size limit; the write was rejected.");
    public static bool Equal(IProperty property, object? before, object? after) =>
        property.GetValueComparer()?.Equals(before, after) ?? Equals(before, after);
    public static string TypeName(IProperty property) => Nullable.GetUnderlyingType(property.ClrType)?.Name ?? property.ClrType.Name;
}
