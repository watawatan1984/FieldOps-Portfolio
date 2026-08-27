using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LinkWorkOrdersToSalesOpportunities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SalesOpportunityId",
                table: "WorkOrders",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_WorkOrders_SalesOpportunityId",
                table: "WorkOrders",
                column: "SalesOpportunityId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkOrders_SalesOpportunities_SalesOpportunityId",
                table: "WorkOrders",
                column: "SalesOpportunityId",
                principalTable: "SalesOpportunities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkOrders_SalesOpportunities_SalesOpportunityId",
                table: "WorkOrders");

            migrationBuilder.DropIndex(
                name: "UX_WorkOrders_SalesOpportunityId",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "SalesOpportunityId",
                table: "WorkOrders");
        }
    }
}