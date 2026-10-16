using AuditTrail.Application.Auditing;
using AuditTrail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditTrail.Infrastructure.Auditing;

public sealed class AuditTrailWriter(AppDbContext db, IDbContextFactory<AppDbContext> factory) : IAuditTrailWriter
{
    public void AddManual(AuditEvent auditEvent) => db.Stage(auditEvent);
    public async Task WriteStandaloneAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        if (System.Transactions.Transaction.Current is not null)
            throw new NotSupportedException("Standalone manual events require a separate, non-ambient transaction scope.");
        await using var standalone = await factory.CreateDbContextAsync(cancellationToken);
        standalone.Stage(auditEvent);
        await standalone.SaveChangesAsync(cancellationToken);
    }
}
