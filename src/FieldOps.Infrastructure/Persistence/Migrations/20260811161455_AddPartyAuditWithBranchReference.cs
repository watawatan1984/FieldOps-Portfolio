using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyAuditWithBranchReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "AuditEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChangeSummary",
                table: "AuditEntries",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "AuditEntries",
                type: "text",
                nullable: false,
                defaultValue: "Success");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_BranchId",
                table: "AuditEntries",
                column: "BranchId");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditEntries_Branches_BranchId",
                table: "AuditEntries",
                column: "BranchId",
                principalTable: "Branches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditEntries_Branches_BranchId",
                table: "AuditEntries");

            migrationBuilder.DropIndex(
                name: "IX_AuditEntries_BranchId",
                table: "AuditEntries");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "AuditEntries");

            migrationBuilder.DropColumn(
                name: "ChangeSummary",
                table: "AuditEntries");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "AuditEntries");
        }
    }
}