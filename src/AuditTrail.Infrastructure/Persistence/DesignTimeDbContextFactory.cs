using AuditTrail.Application.Auditing;
using AuditTrail.Infrastructure.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuditTrail.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Server=localhost;Database=AuditTrailDemo;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options;
        var policy = new AuditOptions();
        return new(options, new BackgroundAuditContext(), new(new(policy), new(policy)), TimeProvider.System, NullLogger<AppDbContext>.Instance);
    }
}
