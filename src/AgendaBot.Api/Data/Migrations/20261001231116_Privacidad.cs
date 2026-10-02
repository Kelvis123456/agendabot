using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendaBot.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Privacidad : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "OptedOutAt",
                table: "Customers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RemindersOptInAt",
                table: "Customers",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OptedOutAt",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "RemindersOptInAt",
                table: "Customers");
        }
    }
}
