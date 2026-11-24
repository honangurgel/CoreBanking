using CoreBanking.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CoreBanking.Infrastructure.Migrations;

[DbContext(typeof(BankingDbContext))]
[Migration("20261124210000_AddPixThrottle")]
public sealed class AddPixThrottle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "PixThrottleSettings" (
                "Id" integer PRIMARY KEY CHECK ("Id" = 1),
                "RequestsPerMinute" integer NOT NULL CHECK ("RequestsPerMinute" BETWEEN 1 AND 100000),
                "MaxConcurrent" integer NOT NULL CHECK ("MaxConcurrent" BETWEEN 1 AND 100)
            );
            INSERT INTO "PixThrottleSettings" VALUES (1, 600, 10);
            CREATE TABLE "PixThrottleCalls" (
                "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "StartedAt" timestamp with time zone NOT NULL
            );
            CREATE INDEX "IX_PixThrottleCalls_StartedAt" ON "PixThrottleCalls" ("StartedAt");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE "PixThrottleCalls";
            DROP TABLE "PixThrottleSettings";
            """);
    }
}
