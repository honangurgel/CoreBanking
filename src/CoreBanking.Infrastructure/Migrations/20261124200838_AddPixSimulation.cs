using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CoreBanking.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPixSimulation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PixKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PixKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PixKeys_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PixOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstitutionIspb = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Direction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Scenario = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReservationEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    SettlementEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PixOperations", x => x.Id);
                    table.CheckConstraint("CK_PixOperations_Amount", "\"AmountMinor\" > 0 AND \"AmountMinor\" <= 99999999999999999");
                    table.CheckConstraint("CK_PixOperations_Attempts", "\"Attempts\" BETWEEN 0 AND 3");
                    table.CheckConstraint("CK_PixOperations_Direction", "\"Direction\" IN ('Outgoing', 'Incoming')");
                    table.CheckConstraint("CK_PixOperations_Scenario", "\"Scenario\" IN ('Confirm', 'Reject')");
                    table.CheckConstraint("CK_PixOperations_Status", "\"Status\" IN ('Pending', 'Confirmed', 'Rejected', 'RequiresReview', 'InsufficientFunds')");
                    table.ForeignKey(
                        name: "FK_PixOperations_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PixTransitions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PixTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PixTransitions_PixOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "PixOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PixKeys_AccountId",
                table: "PixKeys",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PixOperations_AccountId",
                table: "PixOperations",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PixOperations_IdempotencyKey",
                table: "PixOperations",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PixOperations_Status_NextAttemptAt",
                table: "PixOperations",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PixTransitions_OperationId",
                table: "PixTransitions",
                column: "OperationId");

            migrationBuilder.Sql("""
                INSERT INTO "LedgerAccounts" ("Id", "CustomerAccountId", "BalanceMinor")
                VALUES ('9c48bd10-5d28-4c64-94da-aaf778039200', NULL, 0),
                       ('9c48bd10-5d28-4c64-94da-aaf778039201', NULL, 0)
                ON CONFLICT ("Id") DO NOTHING;
                DO $$
                BEGIN
                    IF (SELECT count(*) FROM "LedgerAccounts"
                        WHERE "Id" IN ('9c48bd10-5d28-4c64-94da-aaf778039200', '9c48bd10-5d28-4c64-94da-aaf778039201')
                        AND "CustomerAccountId" IS NULL) <> 2 THEN
                        RAISE EXCEPTION 'Invalid simulated Pix ledger accounts';
                    END IF;
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PixKeys");

            migrationBuilder.DropTable(
                name: "PixTransitions");

            migrationBuilder.DropTable(
                name: "PixOperations");
        }
    }
}
