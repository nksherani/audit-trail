using AuditTrail.Infrastructure.Auditing;
using Microsoft.EntityFrameworkCore;

namespace AuditTrail.Infrastructure.Persistence;

internal static class AuditingModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<AuditRecord>(e =>
        {
            e.ToTable("AuditTrail", table =>
            {
                table.HasCheckConstraint("CK_AuditTrail_RecordType", "[AuditRecordType] IN ('Auto','Manual')");
                table.HasCheckConstraint("CK_AuditTrail_Operation", "[OperationType] IN ('Insert','Update','Delete','Custom')");
                table.HasCheckConstraint("CK_AuditTrail_Actor", "[TriggeredBy] IN ('User','System','Integration')");
                table.HasCheckConstraint("CK_AuditTrail_AutoPk", "[AuditRecordType] <> 'Auto' OR ([PkId] IS NOT NULL AND [PkId] <> '00000000-0000-0000-0000-000000000000')");
                table.HasCheckConstraint("CK_AuditTrail_Metadata", "[Metadata] IS NULL OR ISJSON([Metadata]) = 1");
                table.HasCheckConstraint("CK_AuditTrail_Utc", "DATEPART(TZOFFSET,[Timestamp]) = 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.EntityName).HasMaxLength(256).IsRequired();
            e.Property(x => x.ColumnName).HasMaxLength(256);
            e.Property(x => x.DataType).HasMaxLength(128);
            e.Property(x => x.Timestamp).HasPrecision(7);
            e.Property(x => x.TriggeredBy).HasMaxLength(100).IsRequired();
            e.Property(x => x.UserId).HasMaxLength(256);
            e.Property(x => x.AuditRecordType).HasMaxLength(20).IsRequired();
            e.Property(x => x.OperationType).HasMaxLength(30).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.Property(x => x.Source).HasMaxLength(100);
            e.Property(x => x.EventName).HasMaxLength(150);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.TenantId).HasMaxLength(100);
            e.HasIndex(x => new { x.TenantId, x.EntityName, x.PkId, x.Timestamp }).IsDescending(false, false, false, true);
            e.HasIndex(x => new { x.UserId, x.Timestamp }).IsDescending(false, true);
            e.HasIndex(x => x.OperationId);
            e.HasIndex(x => x.CorrelationId);
            e.HasIndex(x => x.Timestamp).IsDescending();
        });
        model.Entity<AuditOperation>(e =>
        {
            e.ToTable("AuditOperations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TenantId).HasMaxLength(100);
            e.Property(x => x.Kind).HasMaxLength(30);
            e.Property(x => x.Fingerprint).HasMaxLength(64);
            e.HasIndex(x => x.Timestamp);
        });
    }
}
