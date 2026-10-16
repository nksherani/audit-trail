using Microsoft.Data.SqlClient;

namespace AuditTrail.Tests;

[Collection("SQL Server")]
public sealed class SqlPermissionsTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Dedicated_writer_role_cannot_update_or_delete_audit_history()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "AuditTrail.slnx"))) root = root.Parent!;
        await using var connection = new SqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        await using var setup = connection.CreateCommand();
        setup.CommandText = await File.ReadAllTextAsync(Path.Combine(root.FullName, "scripts", "sql", "audit-permissions.sql"));
        await setup.ExecuteNonQueryAsync();
        setup.CommandText = "CREATE USER [AuditWriterPermissionTest] WITHOUT LOGIN; ALTER ROLE [AuditAppWriter] ADD MEMBER [AuditWriterPermissionTest]; EXECUTE AS USER = 'AuditWriterPermissionTest';";
        await setup.ExecuteNonQueryAsync();
        try
        {
            foreach (var sql in new[] { "UPDATE dbo.AuditTrail SET Description = 'tampering' WHERE 1 = 0", "DELETE FROM dbo.AuditTrail WHERE 1 = 0", "DELETE FROM dbo.AuditOperations WHERE 1 = 0" })
            {
                await using var command = connection.CreateCommand(); command.CommandText = sql;
                var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal(229, exception.Number);
            }
        }
        finally { setup.CommandText = "REVERT;"; await setup.ExecuteNonQueryAsync(); }
    }
}
