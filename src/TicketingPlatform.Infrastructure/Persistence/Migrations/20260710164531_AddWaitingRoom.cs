using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketingPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWaitingRoom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WaitingRoomEnabled",
                table: "Events",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WaitingRoomEnabled",
                table: "Events");
        }
    }
}
