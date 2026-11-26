using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CoreBanking.Infrastructure.Migrations;

[DbContext(typeof(BankingDbContext))]
[Migration("20261126130000_AddOperationAudit")]
public sealed class AddOperationAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "OperationAuditEvents" (
                "Id" uuid PRIMARY KEY,
                "OccurredAt" timestamptz NOT NULL,
                "Action" varchar(32) NOT NULL,
                "Outcome" varchar(32) NOT NULL,
                "OperationId" uuid NOT NULL,
                "ActorKind" varchar(20) NOT NULL,
                "Issuer" varchar(512),
                "Subject" varchar(256),
                "RequestId" varchar(128) NOT NULL,
                CHECK ("Action" IN ('Deposit', 'Transfer', 'PixCreate', 'PixRetry')),
                CHECK (("Action" = 'Deposit' AND "Outcome" = 'Completed')
                    OR ("Action" = 'Transfer' AND "Outcome" IN ('Completed', 'InsufficientFunds'))
                    OR ("Action" = 'PixCreate' AND "Outcome" IN ('Pending', 'InsufficientFunds'))
                    OR ("Action" = 'PixRetry' AND "Outcome" = 'Pending')),
                CHECK (length(trim("RequestId")) > 0),
                CHECK (("ActorKind" = 'LocalAnonymous' AND "Issuer" IS NULL AND "Subject" IS NULL)
                    OR ("ActorKind" = 'Oidc' AND "Issuer" IS NOT NULL AND length(trim("Issuer")) > 0
                        AND "Subject" IS NOT NULL AND length(trim("Subject")) > 0))
            );
            CREATE INDEX "IX_OperationAuditEvents_OperationId_OccurredAt"
                ON "OperationAuditEvents" ("OperationId", "OccurredAt", "Id");
            CREATE FUNCTION protect_operation_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'Operation audit events cannot be modified or deleted';
            END;
            $$;
            CREATE TRIGGER protect_operation_audit_trigger
                BEFORE UPDATE OR DELETE ON "OperationAuditEvents"
                FOR EACH ROW EXECUTE FUNCTION protect_operation_audit();
            CREATE TRIGGER protect_operation_audit_truncate_trigger
                BEFORE TRUNCATE ON "OperationAuditEvents"
                FOR EACH STATEMENT EXECUTE FUNCTION protect_operation_audit();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            LOCK TABLE "OperationAuditEvents" IN ACCESS EXCLUSIVE MODE;
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM "OperationAuditEvents") THEN
                    RAISE EXCEPTION 'Cannot remove a non-empty audit table';
                END IF;
            END;
            $$;
            DROP TABLE "OperationAuditEvents";
            DROP FUNCTION protect_operation_audit();
            """);
    }
}
