using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketingPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurablePaymentState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_HoldId",
                table: "Orders");

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Orders",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaymentAttemptedAt",
                table: "Holds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaymentLeaseUntil",
                table: "Holds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaymentReconciledAt",
                table: "Holds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Holds",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_HoldId",
                table: "Orders",
                column: "HoldId",
                unique: true,
                filter: "\"Status\" IN ('PendingPayment', 'Confirmed', 'Refunded')");

            migrationBuilder.CreateIndex(
                name: "IX_Holds_Status_PaymentLeaseUntil",
                table: "Holds",
                columns: new[] { "Status", "PaymentLeaseUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_HoldId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Holds_Status_PaymentLeaseUntil",
                table: "Holds");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PaymentAttemptedAt",
                table: "Holds");

            migrationBuilder.DropColumn(
                name: "PaymentLeaseUntil",
                table: "Holds");

            migrationBuilder.DropColumn(
                name: "PaymentReconciledAt",
                table: "Holds");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Holds");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_HoldId",
                table: "Orders",
                column: "HoldId");
        }
    }
}
