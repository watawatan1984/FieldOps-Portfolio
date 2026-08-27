using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceWorkEventUpdateAppendOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS "TR_WorkEvents_AppendOnly" ON "WorkEvents";
                CREATE TRIGGER "TR_WorkEvents_AppendOnly"
                    BEFORE UPDATE OR DELETE ON "WorkEvents"
                    FOR EACH ROW
                    EXECUTE FUNCTION "fieldops_reject_historical_delete"();

                COMMENT ON TRIGGER "TR_WorkEvents_AppendOnly" ON "WorkEvents" IS
                    'Rejects changes to append-only work history.';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Append-only history hardening is intentionally monotonic.
        }
    }
}