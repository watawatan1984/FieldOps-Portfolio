using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeWorkHistorySearchText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SearchTextNormalized",
                table: "WorkEvents",
                type: "text",
                nullable: true,
                computedColumnSql: "upper(regexp_replace(normalize(btrim(\"Summary\"), NFKC), '[[:space:]]+', ' ', 'g'))",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "SearchTextNormalized",
                table: "Sites",
                type: "text",
                nullable: true,
                computedColumnSql: "upper(regexp_replace(normalize(btrim(\"Name\"), NFKC), '[[:space:]]+', ' ', 'g'))",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "SearchTextNormalized",
                table: "Parties",
                type: "text",
                nullable: true,
                computedColumnSql: "upper(regexp_replace(normalize(btrim(COALESCE(\"OrganizationName\", \"LastName\" || ' ' || \"FirstName\")), NFKC), '[[:space:]]+', ' ', 'g'))",
                stored: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SearchTextNormalized",
                table: "WorkEvents");

            migrationBuilder.DropColumn(
                name: "SearchTextNormalized",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "SearchTextNormalized",
                table: "Parties");
        }
    }
}