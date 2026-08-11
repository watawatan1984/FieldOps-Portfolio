using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesOpportunityOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "SalesOpportunities",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOpportunities_OwnerUserId",
                table: "SalesOpportunities",
                column: "OwnerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_SalesOpportunities_AspNetUsers_OwnerUserId",
                table: "SalesOpportunities",
                column: "OwnerUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SalesOpportunities_AspNetUsers_OwnerUserId",
                table: "SalesOpportunities");

            migrationBuilder.DropIndex(
                name: "IX_SalesOpportunities_OwnerUserId",
                table: "SalesOpportunities");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "SalesOpportunities");
        }
    }
}