using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketingPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RequireTicketTypePerformance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The contract step. Re-runs the SAME backfill the expand step used - it is idempotent
            // by construction, which is exactly what makes a second pass safe and is why it was
            // written that way. This pass catches anything created between that migration and the
            // deploy that moved writes onto performances.
            migrationBuilder.Sql(PerformanceBackfill.CreateOnePerformancePerEvent);
            migrationBuilder.Sql(PerformanceBackfill.LinkTicketTypesToTheirPerformance);

            // No defaultValue on purpose: the scaffolded version proposed the all-zeros Guid, which
            // would either violate the foreign key or, without one, quietly attach ticket types to a
            // date that does not exist. SET NOT NULL failing loudly on a leftover NULL is the
            // outcome to want - it means the backfill missed a row and the deploy should stop.
            migrationBuilder.AlterColumn<Guid>(
                name: "PerformanceId",
                table: "TicketTypes",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the constraint comes off. The backfilled rows stay: deleting dates that tickets
            // now point at, to undo a constraint change, would be destroying real data.
            migrationBuilder.AlterColumn<Guid>(
                name: "PerformanceId",
                table: "TicketTypes",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }
    }
}
