using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basi.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStoryEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StoryEntities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    NovelId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetLanguage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceTerm = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedSourceTerm = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TranslatedTerm = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Aliases = table.Column<string>(type: "jsonb", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsLocked = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeenOrder = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoryEntities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoryEntities_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StoryEntities_Novels_NovelId",
                        column: x => x.NovelId,
                        principalTable: "Novels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoryEntities_NovelId_TargetLanguage_NormalizedSourceTerm",
                table: "StoryEntities",
                columns: new[] { "NovelId", "TargetLanguage", "NormalizedSourceTerm" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoryEntities_OwnerId",
                table: "StoryEntities",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoryEntities");
        }
    }
}
