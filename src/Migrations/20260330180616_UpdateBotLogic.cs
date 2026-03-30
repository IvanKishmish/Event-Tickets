using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTickets.Migrations
{
    /// <inheritdoc />
    public partial class UpdateBotLogic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "TicketOrders",
                type: "bytea",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "TicketOrders");
        }
    }
}
