using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketingPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropEventStartsAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The end of expand -> migrate -> contract. Safe to drop only because the date was added
            // elsewhere, backfilled, had every read moved onto it, and became required first; each of
            // those shipped on its own. Ordering moves to Performances' (EventId, StartsAt) index, so
            // what the Events table still needs is the visibility-and-category filter.
            migrationBuilder.DropIndex(
                name: "IX_Events_Status_Category_StartsAt",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "StartsAt",
                table: "Events");

            migrationBuilder.CreateIndex(
                name: "IX_Events_Status_Category",
                table: "Events",
                columns: new[] { "Status", "Category" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Events_Status_Category",
                table: "Events");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartsAt",
                table: "Events",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Restore the DATA, not just the column. The scaffolded default leaves every event
            // sitting at year 1, so a rollback would come back up with a catalog of nonsense dates -
            // silently, because nothing would fail. Re-derive each event's date from its earliest
            // performance, which is where the value went in the first place.
            migrationBuilder.Sql("""
                UPDATE "Events" e
                SET "StartsAt" = earliest."StartsAt"
                FROM (
                    SELECT DISTINCT ON (p."EventId") p."EventId", p."StartsAt"
                    FROM "Performances" p
                    ORDER BY p."EventId", p."StartsAt", p."Id"
                ) AS earliest
                WHERE earliest."EventId" = e."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Events_Status_Category_StartsAt",
                table: "Events",
                columns: new[] { "Status", "Category", "StartsAt" });
        }
    }
}
