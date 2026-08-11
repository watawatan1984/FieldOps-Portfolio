using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindHistoryDeleteBypassToTransaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION "fieldops_reject_historical_delete"() RETURNS trigger
                LANGUAGE plpgsql
                AS $fieldops$
                BEGIN
                    IF current_setting('fieldops.allow_historical_delete', true) IS DISTINCT FROM txid_current()::text THEN
                        RAISE EXCEPTION 'Historical WorkEvent and AuditEntry rows are append-only.'
                            USING ERRCODE = '42501';
                    END IF;

                    RETURN OLD;
                END;
                $fieldops$;

                COMMENT ON FUNCTION "fieldops_reject_historical_delete"() IS
                    'Rejects historical deletes unless the current transaction explicitly presents its own txid token.';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Security hardening is intentionally monotonic: rollback must not restore a session-reusable bypass.
        }
    }
}
