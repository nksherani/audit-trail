using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AuditTrail.Tests;

[Collection("SQL Server")]
public sealed class MigrationDeploymentTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Idempotent_deployment_script_can_be_applied_repeatedly_to_upgraded_database()
    {
        await using var scope = fixture.Open();
        var script = scope.Db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        await using var connection = new SqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        for (var attempt = 0; attempt < 2; attempt++)
            foreach (var batch in Regex.Split(script, @"^GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                await using var command = connection.CreateCommand(); command.CommandText = batch;
                await command.ExecuteNonQueryAsync();
            }
    }
}
