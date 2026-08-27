using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistResourceAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignedUserId",
                table: "WorkOrders",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedUserId",
                table: "SalesOpportunities",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_AssignedUserId",
                table: "WorkOrders",
                column: "AssignedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOpportunities_AssignedUserId",
                table: "SalesOpportunities",
                column: "AssignedUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_SalesOpportunities_AspNetUsers_AssignedUserId",
                table: "SalesOpportunities",
                column: "AssignedUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkOrders_AspNetUsers_AssignedUserId",
                table: "WorkOrders",
                column: "AssignedUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SalesOpportunities_AspNetUsers_AssignedUserId",
                table: "SalesOpportunities");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkOrders_AspNetUsers_AssignedUserId",
                table: "WorkOrders");

            migrationBuilder.DropIndex(
                name: "IX_WorkOrders_AssignedUserId",
                table: "WorkOrders");

            migrationBuilder.DropIndex(
                name: "IX_SalesOpportunities_AssignedUserId",
                table: "SalesOpportunities");

            migrationBuilder.DropColumn(
                name: "AssignedUserId",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "AssignedUserId",
                table: "SalesOpportunities");
        }
    }
}