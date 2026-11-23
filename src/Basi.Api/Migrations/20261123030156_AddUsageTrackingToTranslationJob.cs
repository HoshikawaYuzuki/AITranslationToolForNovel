using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basi.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageTrackingToTranslationJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompletionTokens",
                table: "TranslationJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PromptTokens",
                table: "TranslationJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalTokens",
                table: "TranslationJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletionTokens",
                table: "TranslationJobs");

            migrationBuilder.DropColumn(
                name: "PromptTokens",
                table: "TranslationJobs");

            migrationBuilder.DropColumn(
                name: "TotalTokens",
                table: "TranslationJobs");
        }
    }
}
