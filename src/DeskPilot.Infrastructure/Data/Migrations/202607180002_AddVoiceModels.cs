using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeskPilot.Infrastructure.Data.Migrations;

/// <inheritdoc />
[DbContext(typeof(DeskPilotDbContext))]
[Migration("202607180002_AddVoiceModels")]
public partial class AddVoiceModels : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "InstalledVoiceModels",
            columns: table => new
            {
                ProviderId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Version = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                ModelId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                RelativePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                IsLastKnownGood = table.Column<bool>(type: "INTEGER", nullable: false),
                InstalledAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_InstalledVoiceModels", x => new { x.ProviderId, x.Version }));

        migrationBuilder.CreateIndex(
            name: "IX_InstalledVoiceModels_ProviderId",
            table: "InstalledVoiceModels",
            column: "ProviderId",
            unique: true,
            filter: "\"IsActive\" = 1");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "InstalledVoiceModels");
}
