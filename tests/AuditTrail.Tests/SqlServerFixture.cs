using AuditTrail.Application.Auditing;
using AuditTrail.Infrastructure;
using AuditTrail.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuditTrail.Tests;

[CollectionDefinition("SQL Server", DisableParallelization = true)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;

public sealed class SqlServerFixture : IAsyncLifetime
{
    public ServiceProvider Services { get; private set; } = null!;
    public string ConnectionString { get; }
    public SqlServerFixture()
    {
        var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("AUDIT_TEST_SQLSERVER")
            ?? "Server=localhost;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True");
        connection.InitialCatalog = "AuditTrailTests_" + Guid.NewGuid().ToString("N");
        connection.MultipleActiveResultSets = false;
        ConnectionString = connection.ConnectionString;
    }
    public async Task InitializeAsync()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddScoped<IAuditAccessProvider, TestAuditAccess>();
        services.AddAuditTrailInfrastructure(ConnectionString);
        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = Open();
        await scope.Db.Database.MigrateAsync();
    }
    public TestScope Open(string? tenant = null, AuditContextInfo? context = null) => new(Services.CreateAsyncScope(),
        context ?? new AuditContextInfo("System", Source: "IntegrationTests", TenantId: tenant ?? Guid.NewGuid().ToString("N")));
    public async Task DisposeAsync()
    {
        // Only a freshly generated test database can be deleted by this fixture.
        var name = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;
        if (!name.StartsWith("AuditTrailTests_", StringComparison.Ordinal) || name.Length != 48)
            throw new InvalidOperationException("Refusing to delete a database outside the test prefix.");
        await using var scope = Open();
        await scope.Db.Database.EnsureDeletedAsync();
        await Services.DisposeAsync();
    }
}
public sealed class TestScope : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;
    public TestScope(AsyncServiceScope scope, AuditContextInfo context)
    { _scope = scope; scope.ServiceProvider.GetRequiredService<BackgroundAuditContext>().Current = context; }
    public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();
    public AppDbContext Db => Get<AppDbContext>();
    public string? Tenant => Get<BackgroundAuditContext>().Current.TenantId;
    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}
public sealed class TestAuditAccess(IAuditContextProvider provider) : IAuditAccessProvider
{
    public bool CanReadHistory { get; set; } = true;
    public string? TenantId => provider.GetCurrent().TenantId;
}
