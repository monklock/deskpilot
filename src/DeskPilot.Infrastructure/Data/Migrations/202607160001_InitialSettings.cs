using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeskPilot.Infrastructure.Data.Migrations;

/// <inheritdoc />
[DbContext(typeof(DeskPilotDbContext))]
[Migration("202607160001_InitialSettings")]
public partial class InitialSettings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ApplicationSettings",
            columns: table => new
            {
                Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Value = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_ApplicationSettings", x => x.Key));

        migrationBuilder.CreateTable(
            name: "VoiceSettings",
            columns: table => new
            {
                Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Value = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_VoiceSettings", x => x.Key));
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ApplicationSettings");
        migrationBuilder.DropTable(name: "VoiceSettings");
    }
}
