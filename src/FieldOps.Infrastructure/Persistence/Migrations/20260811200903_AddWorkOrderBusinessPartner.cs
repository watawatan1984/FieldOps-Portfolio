using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkOrderBusinessPartner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BusinessPartnerId",
                table: "WorkOrders",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_BusinessPartnerId_BranchId",
                table: "WorkOrders",
                columns: new[] { "BusinessPartnerId", "BranchId" });

            migrationBuilder.AddForeignKey(
                name: "FK_WorkOrders_Parties_BusinessPartnerId",
                table: "WorkOrders",
                column: "BusinessPartnerId",
                principalTable: "Parties",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkOrders_Parties_BusinessPartnerId",
                table: "WorkOrders");

            migrationBuilder.DropIndex(
                name: "IX_WorkOrders_BusinessPartnerId_BranchId",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "BusinessPartnerId",
                table: "WorkOrders");
        }
    }
}