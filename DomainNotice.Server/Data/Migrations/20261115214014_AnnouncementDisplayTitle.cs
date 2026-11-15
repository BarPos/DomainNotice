using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DomainNotice.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnnouncementDisplayTitle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayTitle",
                table: "Announcements",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            // Until 1.0.3 the one title was shown on the PCs too: keep showing it there.
            migrationBuilder.Sql("UPDATE \"Announcements\" SET \"DisplayTitle\" = \"Title\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayTitle",
                table: "Announcements");
        }
    }
}
