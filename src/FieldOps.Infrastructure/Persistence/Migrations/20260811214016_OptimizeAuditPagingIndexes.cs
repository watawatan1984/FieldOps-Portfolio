using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeAuditPagingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEntries_BranchId",
                table: "AuditEntries");

            migrationBuilder.DropIndex(
                name: "IX_AuditEntries_OccurredAtUtc_ActorUserId",
                table: "AuditEntries");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_BranchId_OccurredAtUtc_Id",
                table: "AuditEntries",
                columns: new[] { "BranchId", "OccurredAtUtc", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_OccurredAtUtc_Id",
                table: "AuditEntries",
                columns: new[] { "OccurredAtUtc", "Id" },
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEntries_BranchId_OccurredAtUtc_Id",
                table: "AuditEntries");

            migrationBuilder.DropIndex(
                name: "IX_AuditEntries_OccurredAtUtc_Id",
                table: "AuditEntries");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_BranchId",
                table: "AuditEntries",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_OccurredAtUtc_ActorUserId",
                table: "AuditEntries",
                columns: new[] { "OccurredAtUtc", "ActorUserId" },
                descending: new[] { true, false });
        }
    }
}