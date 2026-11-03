using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class DropResearchNoteSymbolList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_EquityResearchNotes_Scope",
                table: "EquityResearchNotes");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EquityResearchNotes_Scope",
                table: "EquityResearchNotes",
                sql: "\"Mode\" IN ('forward','retrospective') AND \"HorizonSessions\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_EquityResearchNotes_Scope",
                table: "EquityResearchNotes");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EquityResearchNotes_Scope",
                table: "EquityResearchNotes",
                sql: "\"Symbol\" IN ('MBB','BID') AND \"Mode\" IN ('forward','retrospective') AND \"HorizonSessions\" > 0");
        }
    }
}
