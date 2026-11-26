using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CoreBanking.Infrastructure.Migrations;

[DbContext(typeof(BankingDbContext))]
[Migration("20261126140000_AddAccountAccess")]
public sealed class AddAccountAccess : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "AccountAccessGrants" (
                "AccountId" uuid NOT NULL REFERENCES "Accounts" ("Id") ON DELETE RESTRICT,
                "Issuer" varchar(512) NOT NULL CHECK (length(trim("Issuer")) > 0),
                "Subject" varchar(256) NOT NULL CHECK (length(trim("Subject")) > 0),
                PRIMARY KEY ("AccountId", "Issuer", "Subject")
            );
            CREATE TABLE "AccountAccessAudit" (
                "Id" uuid PRIMARY KEY,
                "OccurredAt" timestamptz NOT NULL,
                "AccountId" uuid NOT NULL REFERENCES "Accounts" ("Id") ON DELETE RESTRICT,
                "Action" varchar(10) NOT NULL CHECK ("Action" IN ('Grant', 'Revoke')),
                "TargetIssuer" varchar(512) NOT NULL,
                "TargetSubject" varchar(256) NOT NULL,
                "ActorIssuer" varchar(512) NOT NULL,
                "ActorSubject" varchar(256) NOT NULL,
                "RequestId" varchar(128) NOT NULL
            );
            CREATE FUNCTION protect_account_access_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'Account access audit cannot be modified or deleted';
            END;
            $$;
            CREATE TRIGGER protect_account_access_audit_rows
                BEFORE UPDATE OR DELETE ON "AccountAccessAudit"
                FOR EACH ROW EXECUTE FUNCTION protect_account_access_audit();
            CREATE TRIGGER protect_account_access_audit_truncate
                BEFORE TRUNCATE ON "AccountAccessAudit"
                FOR EACH STATEMENT EXECUTE FUNCTION protect_account_access_audit();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            LOCK TABLE "AccountAccessGrants", "AccountAccessAudit" IN ACCESS EXCLUSIVE MODE;
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM "AccountAccessGrants") OR EXISTS (SELECT 1 FROM "AccountAccessAudit") THEN
                    RAISE EXCEPTION 'Cannot remove account access data';
                END IF;
            END;
            $$;
            DROP TABLE "AccountAccessAudit";
            DROP FUNCTION protect_account_access_audit();
            DROP TABLE "AccountAccessGrants";
            """);
    }
}
