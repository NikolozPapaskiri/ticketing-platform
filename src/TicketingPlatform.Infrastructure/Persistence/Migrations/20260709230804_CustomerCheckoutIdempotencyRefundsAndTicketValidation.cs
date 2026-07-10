using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketingPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomerCheckoutIdempotencyRefundsAndTicketValidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "Tickets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScannedAt",
                table: "Tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Tickets",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Issued");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VoidedAt",
                table: "Tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LockedBy",
                table: "OutboxMessages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedUntil",
                table: "OutboxMessages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerUserId",
                table: "Orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderRefundId",
                table: "Orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RefundedAt",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerUserId",
                table: "Holds",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IdempotencyRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyRecords", x => x.Id);
                });

            migrationBuilder.Sql("""
                UPDATE "Tickets"
                SET "Code" = replace("Id"::text, '-', '')
                WHERE "Code" IS NULL
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Code",
                table: "Tickets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Code",
                table: "Tickets",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedAt_LockedUntil_OccurredAt",
                table: "OutboxMessages",
                columns: new[] { "ProcessedAt", "LockedUntil", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CustomerUserId",
                table: "Orders",
                column: "CustomerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Holds_CustomerUserId",
                table: "Holds",
                column: "CustomerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_OrderId",
                table: "IdempotencyRecords",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_TenantId_ActorKey_Key",
                table: "IdempotencyRecords",
                columns: new[] { "TenantId", "ActorKey", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdempotencyRecords");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_Code",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_ProcessedAt_LockedUntil_OccurredAt",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_Orders_CustomerUserId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Holds_CustomerUserId",
                table: "Holds");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ScannedAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "VoidedAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "LockedBy",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "LockedUntil",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "CustomerUserId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ProviderRefundId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RefundedAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CustomerUserId",
                table: "Holds");
        }
    }
}
