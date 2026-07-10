using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketingPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundPendingAndTicketConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_HoldId",
                table: "Orders");

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Tickets",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_HoldId",
                table: "Orders",
                column: "HoldId",
                unique: true,
                filter: "\"Status\" IN ('PendingPayment', 'Confirmed', 'RefundPending', 'Refunded')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_HoldId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Tickets");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_HoldId",
                table: "Orders",
                column: "HoldId",
                unique: true,
                filter: "\"Status\" IN ('PendingPayment', 'Confirmed', 'Refunded')");
        }
    }
}
