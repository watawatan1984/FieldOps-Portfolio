using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDemoDatasetMarker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DemoDatasetMarkers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DatasetIdentifier = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DatasetVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    InstalledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoDatasetMarkers", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "DemoDatasetMarkers",
                columns: new[] { "Id", "DatasetIdentifier", "DatasetVersion", "InstalledAtUtc" },
                values: new object[]
                {
                    new Guid("90000000-0000-4000-8000-000000000001"),
                    "fieldops-portal-fictional-demo",
                    "1",
                    new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DemoDatasetMarkers");
        }
    }
}