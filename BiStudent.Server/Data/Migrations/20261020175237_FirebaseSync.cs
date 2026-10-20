using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BiStudent.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class FirebaseSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClaimToken",
                table: "Teachers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirebaseUid",
                table: "Teachers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SyncAliases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Table = table.Column<string>(type: "TEXT", nullable: false),
                    CloudId = table.Column<string>(type: "TEXT", nullable: false),
                    CanonicalCloudId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncAliases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Table = table.Column<string>(type: "TEXT", nullable: false),
                    LocalId = table.Column<int>(type: "INTEGER", nullable: false),
                    CloudId = table.Column<string>(type: "TEXT", nullable: false),
                    CloudUpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncOutbox",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Table = table.Column<string>(type: "TEXT", nullable: false),
                    LocalId = table.Column<int>(type: "INTEGER", nullable: false),
                    CloudId = table.Column<string>(type: "TEXT", nullable: true),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncOutbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Teachers_FirebaseUid",
                table: "Teachers",
                column: "FirebaseUid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncAliases_Table_CloudId",
                table: "SyncAliases",
                columns: new[] { "Table", "CloudId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncLinks_Table_CloudId",
                table: "SyncLinks",
                columns: new[] { "Table", "CloudId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncLinks_Table_LocalId",
                table: "SyncLinks",
                columns: new[] { "Table", "LocalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncOutbox_Table_LocalId",
                table: "SyncOutbox",
                columns: new[] { "Table", "LocalId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncAliases");

            migrationBuilder.DropTable(
                name: "SyncLinks");

            migrationBuilder.DropTable(
                name: "SyncOutbox");

            migrationBuilder.DropIndex(
                name: "IX_Teachers_FirebaseUid",
                table: "Teachers");

            migrationBuilder.DropColumn(
                name: "ClaimToken",
                table: "Teachers");

            migrationBuilder.DropColumn(
                name: "FirebaseUid",
                table: "Teachers");
        }
    }
}
