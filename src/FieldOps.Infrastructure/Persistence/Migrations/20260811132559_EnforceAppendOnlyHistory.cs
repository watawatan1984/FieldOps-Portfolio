using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceAppendOnlyHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE FUNCTION "fieldops_reject_historical_delete"() RETURNS trigger
                LANGUAGE plpgsql
                AS $fieldops$
                BEGIN
                    IF current_setting('fieldops.allow_historical_delete', true) IS DISTINCT FROM 'on' THEN
                        RAISE EXCEPTION 'Historical WorkEvent and AuditEntry rows are append-only.'
                            USING ERRCODE = '42501';
                    END IF;

                    RETURN OLD;
                END;
                $fieldops$;

                COMMENT ON FUNCTION "fieldops_reject_historical_delete"() IS
                    'Rejects historical deletes unless a deliberate transaction-local demo-reset bypass is enabled.';

                CREATE TRIGGER "TR_WorkEvents_AppendOnly"
                    BEFORE DELETE ON "WorkEvents"
                    FOR EACH ROW
                    EXECUTE FUNCTION "fieldops_reject_historical_delete"();

                CREATE TRIGGER "TR_AuditEntries_AppendOnly"
                    BEFORE DELETE ON "AuditEntries"
                    FOR EACH ROW
                    EXECUTE FUNCTION "fieldops_reject_historical_delete"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS "TR_WorkEvents_AppendOnly" ON "WorkEvents";
                DROP TRIGGER IF EXISTS "TR_AuditEntries_AppendOnly" ON "AuditEntries";
                DROP FUNCTION IF EXISTS "fieldops_reject_historical_delete"();
                """);
        }
    }
}