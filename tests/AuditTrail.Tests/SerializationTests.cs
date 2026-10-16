using System.Globalization;
using System.Text.Json;
using AuditTrail.Infrastructure.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace AuditTrail.Tests;

public sealed class SerializationTests
{
    private readonly AuditValueSerializer _serializer = new(new());
    [Fact]
    public void Null_empty_and_literal_null_are_distinct()
    {
        Assert.Null(_serializer.Serialize(null));
        Assert.Equal("\"\"", _serializer.Serialize(""));
        Assert.Equal("\"null\"", _serializer.Serialize("null"));
    }
    [Fact]
    public void Scalars_preserve_precision_dates_enums_and_guids_across_cultures()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new("fr-FR");
            Assert.Equal("12345678901234.1234", _serializer.Serialize(12345678901234.1234m));
            var time = DateTimeOffset.Parse("2026-10-16T12:34:56+05:00", CultureInfo.InvariantCulture);
            Assert.Equal(time, JsonSerializer.Deserialize<DateTimeOffset>(_serializer.Serialize(time)!));
            var id = Guid.NewGuid();
            Assert.Equal(id, JsonSerializer.Deserialize<Guid>(_serializer.Serialize(id)!));
            Assert.Equal("\"Friday\"", _serializer.Serialize(DayOfWeek.Friday));
            Assert.Equal("true", _serializer.Serialize(true));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
    [Fact]
    public void Metadata_redacts_nested_secrets_and_orders_properties()
    {
        var json = _serializer.Metadata(new Dictionary<string, object?> { ["Z"] = 1, ["A"] = new { AccessToken = "secret-token", Name = "safe" } });
        Assert.DoesNotContain("secret-token", json);
        Assert.Contains("[REDACTED]", json);
        Assert.True(json!.IndexOf("A", StringComparison.Ordinal) < json.IndexOf("Z", StringComparison.Ordinal));
    }
    [Fact]
    public void Unsupported_object_graphs_and_oversized_payloads_are_rejected()
    {
        Assert.Throws<NotSupportedException>(() => _serializer.Serialize(new { Unsafe = new object() }));
        Assert.Throws<InvalidOperationException>(() => new AuditValueSerializer(new() { MaxValueLength = 4 }).Serialize("long-string"));
    }
    [Fact]
    public void Configured_ef_value_comparer_is_honored_and_explicit_json_is_sanitized()
    {
        var model = new ModelBuilder();
        model.Entity<ComparableValue>().HasKey(x => x.Id);
        model.Entity<ComparableValue>().Property(x => x.Value).Metadata.SetValueComparer(new ValueComparer<string>(
            (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase),
            value => value.ToUpperInvariant().GetHashCode(), value => value));
        var property = model.FinalizeModel().FindEntityType(typeof(ComparableValue))!.FindProperty(nameof(ComparableValue.Value))!;
        Assert.True(AuditValueSerializer.Equal(property, "UNCHANGED", "unchanged"));
        var json = _serializer.Serialize(new { Email = "private@example.com", AccessToken = "private-token" }, allowJson: true);
        Assert.DoesNotContain("private@example.com", json); Assert.DoesNotContain("private-token", json);
    }
    private sealed class ComparableValue { public Guid Id { get; set; } public string Value { get; set; } = ""; }
}
