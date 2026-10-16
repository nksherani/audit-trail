using Microsoft.EntityFrameworkCore.Migrations;

namespace AuditTrail.Infrastructure.Migrations;

public partial class UseApplicationGeneratedGuids
{
    private static void ConvertKeys(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] { "Categories", "Suppliers", "Products" })
        {
            migrationBuilder.AddColumn<Guid>("NewId", table, type: "uniqueidentifier", nullable: true);
            // Backfill historical rows only. New entities generate their IDs in C#.
            migrationBuilder.Sql($"UPDATE [{table}] SET [NewId] = NEWID();");
        }

        migrationBuilder.AddColumn<Guid>("NewCategoryId", "Products", type: "uniqueidentifier", nullable: true);
        migrationBuilder.AddColumn<Guid>("NewSupplierId", "Products", type: "uniqueidentifier", nullable: true);
        migrationBuilder.Sql("""
            UPDATE p SET p.NewCategoryId = c.NewId, p.NewSupplierId = s.NewId
            FROM Products p
            INNER JOIN Categories c ON p.CategoryId = c.Id
            INNER JOIN Suppliers s ON p.SupplierId = s.Id;
            """);

        migrationBuilder.DropForeignKey("FK_Products_Categories_CategoryId", "Products");
        migrationBuilder.DropForeignKey("FK_Products_Suppliers_SupplierId", "Products");
        migrationBuilder.DropIndex("IX_Products_CategoryId", "Products");
        migrationBuilder.DropIndex("IX_Products_SupplierId", "Products");

        foreach (var table in new[] { "Products", "Categories", "Suppliers" })
        {
            migrationBuilder.DropPrimaryKey($"PK_{table}", table);
            migrationBuilder.DropColumn("Id", table);
            migrationBuilder.RenameColumn("NewId", table, "Id");
            migrationBuilder.AlterColumn<Guid>("Id", table, type: "uniqueidentifier", nullable: false,
                oldClrType: typeof(Guid), oldType: "uniqueidentifier", oldNullable: true);
            migrationBuilder.AddPrimaryKey($"PK_{table}", table, "Id");
        }

        foreach (var column in new[] { "CategoryId", "SupplierId" })
        {
            migrationBuilder.DropColumn(column, "Products");
            migrationBuilder.RenameColumn($"New{column}", "Products", column);
            migrationBuilder.AlterColumn<Guid>(column, "Products", type: "uniqueidentifier", nullable: false,
                oldClrType: typeof(Guid), oldType: "uniqueidentifier", oldNullable: true);
            migrationBuilder.CreateIndex($"IX_Products_{column}", "Products", column);
        }

        migrationBuilder.AddForeignKey("FK_Products_Categories_CategoryId", "Products", "CategoryId",
            "Categories", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey("FK_Products_Suppliers_SupplierId", "Products", "SupplierId",
            "Suppliers", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
    }
}
