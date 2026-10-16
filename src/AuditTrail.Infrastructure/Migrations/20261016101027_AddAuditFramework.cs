using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditTrail.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditFramework : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InternalNote",
                table: "Suppliers",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Suppliers",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "Suppliers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "Products",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Products",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Products",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "Products",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Categories",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "Categories",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuditOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ResultCount = table.Column<int>(type: "int", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditTrail",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PkId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EntityName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ColumnName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OldValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DataType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    TriggeredBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AuditRecordType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OperationType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EventName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Metadata = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AuditSchemaVersion = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditTrail", x => x.Id);
                    table.CheckConstraint("CK_AuditTrail_Actor", "[TriggeredBy] IN ('User','System','Integration')");
                    table.CheckConstraint("CK_AuditTrail_AutoPk", "[AuditRecordType] <> 'Auto' OR ([PkId] IS NOT NULL AND [PkId] <> '00000000-0000-0000-0000-000000000000')");
                    table.CheckConstraint("CK_AuditTrail_Metadata", "[Metadata] IS NULL OR ISJSON([Metadata]) = 1");
                    table.CheckConstraint("CK_AuditTrail_Operation", "[OperationType] IN ('Insert','Update','Delete','Custom')");
                    table.CheckConstraint("CK_AuditTrail_RecordType", "[AuditRecordType] IN ('Auto','Manual')");
                    table.CheckConstraint("CK_AuditTrail_Utc", "DATEPART(TZOFFSET,[Timestamp]) = 0");
                });

            migrationBuilder.CreateTable(
                name: "ImportCheckpoint",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Cursor = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportCheckpoint", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditOperations_Timestamp",
                table: "AuditOperations",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AuditTrail_CorrelationId",
                table: "AuditTrail",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditTrail_OperationId",
                table: "AuditTrail",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditTrail_TenantId_EntityName_PkId_Timestamp",
                table: "AuditTrail",
                columns: new[] { "TenantId", "EntityName", "PkId", "Timestamp" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuditTrail_Timestamp",
                table: "AuditTrail",
                column: "Timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_AuditTrail_UserId_Timestamp",
                table: "AuditTrail",
                columns: new[] { "UserId", "Timestamp" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditOperations");

            migrationBuilder.DropTable(
                name: "AuditTrail");

            migrationBuilder.DropTable(
                name: "ImportCheckpoint");

            migrationBuilder.DropColumn(
                name: "InternalNote",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Categories");
        }
    }
}
