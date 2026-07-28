using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketingPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LinkTicketTypeToPerformance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PerformanceId",
                table: "TicketTypes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketTypes_PerformanceId",
                table: "TicketTypes",
                column: "PerformanceId");

            migrationBuilder.AddForeignKey(
                name: "FK_TicketTypes_Performances_PerformanceId",
                table: "TicketTypes",
                column: "PerformanceId",
                principalTable: "Performances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Backfill: every existing event becomes a one-night run, and its ticket types point at
            // that date. Runs AFTER the column exists and BEFORE anything reads it. The statements
            // live in PerformanceBackfill so a test can execute exactly these.
            migrationBuilder.Sql(PerformanceBackfill.CreateOnePerformancePerEvent);
            migrationBuilder.Sql(PerformanceBackfill.LinkTicketTypesToTheirPerformance);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TicketTypes_Performances_PerformanceId",
                table: "TicketTypes");

            migrationBuilder.DropIndex(
                name: "IX_TicketTypes_PerformanceId",
                table: "TicketTypes");

            migrationBuilder.DropColumn(
                name: "PerformanceId",
                table: "TicketTypes");
        }
    }
}
