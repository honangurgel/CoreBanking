using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreBanking.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PersistTraceContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TraceParent",
                table: "PixOperations",
                type: "character varying(55)",
                maxLength: 55,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceParent",
                table: "OutboxMessages",
                type: "character varying(55)",
                maxLength: 55,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TraceParent",
                table: "PixOperations");

            migrationBuilder.DropColumn(
                name: "TraceParent",
                table: "OutboxMessages");
        }
    }
}
