using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeskPilot.Infrastructure.Data.Migrations;

/// <inheritdoc />
[DbContext(typeof(DeskPilotDbContext))]
[Migration("202607180001_AddAudioDevicePreferences")]
public partial class AddAudioDevicePreferences : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AudioDevicePreferences",
            columns: table => new
            {
                Slot = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                EndpointId = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                FriendlyName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_AudioDevicePreferences", x => x.Slot));
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "AudioDevicePreferences");
}
