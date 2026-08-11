CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE TABLE "AuditEntries" (
        "Id" uuid NOT NULL,
        "AggregateType" text NOT NULL,
        "AggregateId" uuid NOT NULL,
        "Action" text NOT NULL,
        "OccurredAtUtc" timestamp with time zone NOT NULL,
        "ActorUserId" text NOT NULL,
        "CreatedAtUtc" timestamp with time zone NOT NULL,
        "UpdatedAtUtc" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_AuditEntries" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE TABLE "Branches" (
        "Id" uuid NOT NULL,
        "Name" text NOT NULL,
        "CreatedAtUtc" timestamp with time zone NOT NULL,
        "UpdatedAtUtc" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Branches" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE TABLE "Parties" (
        "Id" uuid NOT NULL,
        "OrganizationName" text,
        "FirstName" text,
        "LastName" text,
        "NormalizedName" text GENERATED ALWAYS AS (upper(COALESCE("OrganizationName", "LastName" || ' ' || "FirstName"))) STORED,
        "CreatedAtUtc" timestamp with time zone NOT NULL,
        "UpdatedAtUtc" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Parties" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE TABLE "Contacts" (
        "Id" uuid NOT NULL,
        "PartyId" uuid NOT NULL,
        "FirstName" text NOT NULL,
        "LastName" text NOT NULL,
        "IsPrimary" boolean NOT NULL,
        "CreatedAtUtc" timestamp with time zone NOT NULL,
        "UpdatedAtUtc" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Contacts" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_Contacts_Parties_PartyId" FOREIGN KEY ("PartyId") REFERENCES "Parties" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE TABLE "PartyBranchAssignments" (
        "Id" uuid NOT NULL,
        "PartyId" uuid NOT NULL,
        "BranchId" uuid NOT NULL,
        "CreatedAtUtc" timestamp with time zone NOT NULL,
        "UpdatedAtUtc" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_PartyBranchAssignments" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_PartyBranchAssignments_Branches_BranchId" FOREIGN KEY ("BranchId") REFERENCES "Branches" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_PartyBranchAssignments_Parties_PartyId" FOREIGN KEY ("PartyId") REFERENCES "Parties" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE TABLE "PartyRoles" (
        "Id" uuid NOT NULL,
        "PartyId" uuid NOT NULL,
        "RoleType" integer NOT NULL,
        "CreatedAtUtc" timestamp with time zone NOT NULL,
        "UpdatedAtUtc" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_PartyRoles" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_PartyRoles_Parties_PartyId" FOREIGN KEY ("PartyId") REFERENCES "Parties" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE TABLE "Sites" (
        "Id" uuid NOT NULL,
        "PartyId" uuid NOT NULL,
        "BranchId" uuid NOT NULL,
        "Name" text NOT NULL,
        "CreatedAtUtc" timestamp with time zone NOT NULL,
        "UpdatedAtUtc" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Sites" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_Sites_Branches_BranchId" FOREIGN KEY ("BranchId") REFERENCES "Branches" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_Sites_Parties_PartyId" FOREIGN KEY ("PartyId") REFERENCES "Parties" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE TABLE "SalesOpportunities" (
        "Id" uuid NOT NULL,
        "BranchId" uuid NOT NULL,
        "PartyId" uuid NOT NULL,
        "SiteId" uuid NOT NULL,
        "Status" integer NOT NULL,
        "ProposedAmount" numeric(18,2),
        "ExpectedCloseDate" date,
        "CreatedAtUtc" timestamp with time zone NOT NULL,
        "UpdatedAtUtc" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_SalesOpportunities" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_SalesOpportunities_Branches_BranchId" FOREIGN KEY ("BranchId") REFERENCES "Branches" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_SalesOpportunities_Parties_PartyId" FOREIGN KEY ("PartyId") REFERENCES "Parties" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_SalesOpportunities_Sites_SiteId" FOREIGN KEY ("SiteId") REFERENCES "Sites" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE TABLE "WorkOrders" (
        "Id" uuid NOT NULL,
        "BranchId" uuid NOT NULL,
        "PartyId" uuid NOT NULL,
        "SiteId" uuid NOT NULL,
        "Status" integer NOT NULL,
        "ScheduledStartUtc" timestamp with time zone,
        "CreatedAtUtc" timestamp with time zone NOT NULL,
        "UpdatedAtUtc" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_WorkOrders" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_WorkOrders_Branches_BranchId" FOREIGN KEY ("BranchId") REFERENCES "Branches" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_WorkOrders_Parties_PartyId" FOREIGN KEY ("PartyId") REFERENCES "Parties" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_WorkOrders_Sites_SiteId" FOREIGN KEY ("SiteId") REFERENCES "Sites" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE TABLE "WorkEvents" (
        "Id" uuid NOT NULL,
        "WorkOrderId" uuid NOT NULL,
        "EventType" integer NOT NULL,
        "OccurredAtUtc" timestamp with time zone NOT NULL,
        "BranchId" uuid NOT NULL,
        "Summary" text NOT NULL,
        "ActorUserId" text NOT NULL,
        "CreatedAtUtc" timestamp with time zone NOT NULL,
        "UpdatedAtUtc" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_WorkEvents" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_WorkEvents_Branches_BranchId" FOREIGN KEY ("BranchId") REFERENCES "Branches" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_WorkEvents_WorkOrders_WorkOrderId" FOREIGN KEY ("WorkOrderId") REFERENCES "WorkOrders" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE INDEX "IX_AuditEntries_OccurredAtUtc_ActorUserId" ON "AuditEntries" ("OccurredAtUtc" DESC, "ActorUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE INDEX "IX_Contacts_PartyId" ON "Contacts" ("PartyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE INDEX "IX_Parties_NormalizedName" ON "Parties" ("NormalizedName");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE INDEX "IX_PartyBranchAssignments_BranchId" ON "PartyBranchAssignments" ("BranchId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_PartyBranchAssignments_PartyId_BranchId" ON "PartyBranchAssignments" ("PartyId", "BranchId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_PartyRoles_PartyId_RoleType" ON "PartyRoles" ("PartyId", "RoleType");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE INDEX "IX_SalesOpportunities_BranchId_Status_ExpectedCloseDate" ON "SalesOpportunities" ("BranchId", "Status", "ExpectedCloseDate");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE INDEX "IX_SalesOpportunities_PartyId" ON "SalesOpportunities" ("PartyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE INDEX "IX_SalesOpportunities_SiteId" ON "SalesOpportunities" ("SiteId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE INDEX "IX_Sites_BranchId" ON "Sites" ("BranchId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE INDEX "IX_Sites_PartyId" ON "Sites" ("PartyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE INDEX "IX_WorkEvents_BranchId" ON "WorkEvents" ("BranchId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE INDEX "IX_WorkEvents_WorkOrderId_OccurredAtUtc" ON "WorkEvents" ("WorkOrderId", "OccurredAtUtc" DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE INDEX "IX_WorkOrders_BranchId_Status_ScheduledStartUtc" ON "WorkOrders" ("BranchId", "Status", "ScheduledStartUtc");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE INDEX "IX_WorkOrders_PartyId_SiteId" ON "WorkOrders" ("PartyId", "SiteId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    CREATE INDEX "IX_WorkOrders_SiteId" ON "WorkOrders" ("SiteId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811130953_InitialCreate') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260811130953_InitialCreate', '10.0.10');
    END IF;
END $EF$;
COMMIT;
START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811132559_EnforceAppendOnlyHistory') THEN
    CREATE FUNCTION "fieldops_reject_historical_delete"() RETURNS trigger
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

    CREATE TRIGGER "TR_WorkEvents_AppendOnly"
        BEFORE DELETE ON "WorkEvents"
        FOR EACH ROW
        EXECUTE FUNCTION "fieldops_reject_historical_delete"();

    CREATE TRIGGER "TR_AuditEntries_AppendOnly"
        BEFORE DELETE ON "AuditEntries"
        FOR EACH ROW
        EXECUTE FUNCTION "fieldops_reject_historical_delete"();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811132559_EnforceAppendOnlyHistory') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260811132559_EnforceAppendOnlyHistory', '10.0.10');
    END IF;
END $EF$;
COMMIT;
START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811134057_BindHistoryDeleteBypassToTransaction') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811134057_BindHistoryDeleteBypassToTransaction') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260811134057_BindHistoryDeleteBypassToTransaction', '10.0.10');
    END IF;
END $EF$;
COMMIT;
START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811140552_AddDemoIdentity') THEN
    CREATE TABLE "AspNetRoles" (
        "Id" text NOT NULL,
        "Name" character varying(256),
        "NormalizedName" character varying(256),
        "ConcurrencyStamp" text,
        CONSTRAINT "PK_AspNetRoles" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811140552_AddDemoIdentity') THEN
    CREATE TABLE "AspNetUsers" (
        "Id" text NOT NULL,
        "DisplayName" character varying(200) NOT NULL,
        "BranchId" uuid,
        "UserName" character varying(256),
        "NormalizedUserName" character varying(256),
        "Email" character varying(256),
        "NormalizedEmail" character varying(256),
        "EmailConfirmed" boolean NOT NULL,
        "PasswordHash" text,
        "SecurityStamp" text,
        "ConcurrencyStamp" text,
        "PhoneNumber" text,
        "PhoneNumberConfirmed" boolean NOT NULL,
        "TwoFactorEnabled" boolean NOT NULL,
        "LockoutEnd" timestamp with time zone,
        "LockoutEnabled" boolean NOT NULL,
        "AccessFailedCount" integer NOT NULL,
        CONSTRAINT "PK_AspNetUsers" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_AspNetUsers_Branches_BranchId" FOREIGN KEY ("BranchId") REFERENCES "Branches" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811140552_AddDemoIdentity') THEN
    CREATE TABLE "AspNetRoleClaims" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "RoleId" text NOT NULL,
        "ClaimType" text,
        "ClaimValue" text,
        CONSTRAINT "PK_AspNetRoleClaims" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_AspNetRoleClaims_AspNetRoles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "AspNetRoles" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811140552_AddDemoIdentity') THEN
    CREATE TABLE "AspNetUserClaims" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "UserId" text NOT NULL,
        "ClaimType" text,
        "ClaimValue" text,
        CONSTRAINT "PK_AspNetUserClaims" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_AspNetUserClaims_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811140552_AddDemoIdentity') THEN
    CREATE TABLE "AspNetUserLogins" (
        "LoginProvider" text NOT NULL,
        "ProviderKey" text NOT NULL,
        "ProviderDisplayName" text,
        "UserId" text NOT NULL,
        CONSTRAINT "PK_AspNetUserLogins" PRIMARY KEY ("LoginProvider", "ProviderKey"),
        CONSTRAINT "FK_AspNetUserLogins_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811140552_AddDemoIdentity') THEN
    CREATE TABLE "AspNetUserRoles" (
        "UserId" text NOT NULL,
        "RoleId" text NOT NULL,
        CONSTRAINT "PK_AspNetUserRoles" PRIMARY KEY ("UserId", "RoleId"),
        CONSTRAINT "FK_AspNetUserRoles_AspNetRoles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "AspNetRoles" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_AspNetUserRoles_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811140552_AddDemoIdentity') THEN
    CREATE TABLE "AspNetUserTokens" (
        "UserId" text NOT NULL,
        "LoginProvider" text NOT NULL,
        "Name" text NOT NULL,
        "Value" text,
        CONSTRAINT "PK_AspNetUserTokens" PRIMARY KEY ("UserId", "LoginProvider", "Name"),
        CONSTRAINT "FK_AspNetUserTokens_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811140552_AddDemoIdentity') THEN
    CREATE INDEX "IX_AspNetRoleClaims_RoleId" ON "AspNetRoleClaims" ("RoleId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811140552_AddDemoIdentity') THEN
    CREATE UNIQUE INDEX "RoleNameIndex" ON "AspNetRoles" ("NormalizedName");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811140552_AddDemoIdentity') THEN
    CREATE INDEX "IX_AspNetUserClaims_UserId" ON "AspNetUserClaims" ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811140552_AddDemoIdentity') THEN
    CREATE INDEX "IX_AspNetUserLogins_UserId" ON "AspNetUserLogins" ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811140552_AddDemoIdentity') THEN
    CREATE INDEX "IX_AspNetUserRoles_RoleId" ON "AspNetUserRoles" ("RoleId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811140552_AddDemoIdentity') THEN
    CREATE INDEX "EmailIndex" ON "AspNetUsers" ("NormalizedEmail");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811140552_AddDemoIdentity') THEN
    CREATE INDEX "IX_AspNetUsers_BranchId" ON "AspNetUsers" ("BranchId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811140552_AddDemoIdentity') THEN
    CREATE UNIQUE INDEX "UserNameIndex" ON "AspNetUsers" ("NormalizedUserName");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811140552_AddDemoIdentity') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260811140552_AddDemoIdentity', '10.0.10');
    END IF;
END $EF$;
COMMIT;
-- End of migration script.
