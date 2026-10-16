namespace AuditTrail.Domain.Common;

public interface IAuditableEntity;
public abstract class AuditableEntity : IAuditableEntity;
public interface ITenantEntity { string? TenantId { get; set; } }
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditIgnoreAttribute : Attribute;
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditMaskAttribute : Attribute;
// Opt-in only for mapped scalar properties with an explicitly configured JSON converter.
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditJsonAttribute : Attribute;
