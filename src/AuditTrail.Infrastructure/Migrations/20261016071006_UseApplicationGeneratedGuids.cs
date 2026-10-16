using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditTrail.Infrastructure.Migrations;

public partial class UseApplicationGeneratedGuids : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => ConvertKeys(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("GUID keys cannot be converted back to the original integer IDs. Restore a backup to revert this migration.");
}
