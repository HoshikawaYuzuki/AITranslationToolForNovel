using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basi.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStoryMemoryLiteSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "FirstSeenOrder",
                table: "StoryEntities",
                newName: "EvidenceSegmentOrder");

            migrationBuilder.AddColumn<string>(
                name: "ConflictDetails",
                table: "StoryEntities",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceQuote",
                table: "StoryEntities",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EvidenceSegmentId",
                table: "StoryEntities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceSegmentStableId",
                table: "StoryEntities",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "StoryEntities",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Provisional");

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "StoryEntities",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "SceneFacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    NovelId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetLanguage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FactKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedFactKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FactType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConflictDetails = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsLocked = table.Column<bool>(type: "boolean", nullable: false),
                    EvidenceSegmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvidenceSegmentStableId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EvidenceSegmentOrder = table.Column<int>(type: "integer", nullable: true),
                    EvidenceQuote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SceneFacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SceneFacts_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SceneFacts_Novels_NovelId",
                        column: x => x.NovelId,
                        principalTable: "Novels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SceneFacts_Segments_EvidenceSegmentId",
                        column: x => x.EvidenceSegmentId,
                        principalTable: "Segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SpeechObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    NovelId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetLanguage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SpeakerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedSpeakerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AddresseeName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedAddresseeName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SpeechRegister = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PronounStyle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Observation = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConflictDetails = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsLocked = table.Column<bool>(type: "boolean", nullable: false),
                    EvidenceSegmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvidenceSegmentStableId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EvidenceSegmentOrder = table.Column<int>(type: "integer", nullable: true),
                    EvidenceQuote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpeechObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpeechObservations_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SpeechObservations_Novels_NovelId",
                        column: x => x.NovelId,
                        principalTable: "Novels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SpeechObservations_Segments_EvidenceSegmentId",
                        column: x => x.EvidenceSegmentId,
                        principalTable: "Segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoryEntities_EvidenceSegmentId",
                table: "StoryEntities",
                column: "EvidenceSegmentId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneFacts_EvidenceSegmentId",
                table: "SceneFacts",
                column: "EvidenceSegmentId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneFacts_NovelId_TargetLanguage_NormalizedFactKey",
                table: "SceneFacts",
                columns: new[] { "NovelId", "TargetLanguage", "NormalizedFactKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SceneFacts_OwnerId",
                table: "SceneFacts",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_SpeechObservations_EvidenceSegmentId",
                table: "SpeechObservations",
                column: "EvidenceSegmentId");

            migrationBuilder.CreateIndex(
                name: "IX_SpeechObservations_NovelId_TargetLanguage_NormalizedSpeaker~",
                table: "SpeechObservations",
                columns: new[] { "NovelId", "TargetLanguage", "NormalizedSpeakerName", "NormalizedAddresseeName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpeechObservations_OwnerId",
                table: "SpeechObservations",
                column: "OwnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_StoryEntities_Segments_EvidenceSegmentId",
                table: "StoryEntities",
                column: "EvidenceSegmentId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StoryEntities_Segments_EvidenceSegmentId",
                table: "StoryEntities");

            migrationBuilder.DropTable(
                name: "SceneFacts");

            migrationBuilder.DropTable(
                name: "SpeechObservations");

            migrationBuilder.DropIndex(
                name: "IX_StoryEntities_EvidenceSegmentId",
                table: "StoryEntities");

            migrationBuilder.DropColumn(
                name: "ConflictDetails",
                table: "StoryEntities");

            migrationBuilder.DropColumn(
                name: "EvidenceQuote",
                table: "StoryEntities");

            migrationBuilder.DropColumn(
                name: "EvidenceSegmentId",
                table: "StoryEntities");

            migrationBuilder.DropColumn(
                name: "EvidenceSegmentStableId",
                table: "StoryEntities");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "StoryEntities");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "StoryEntities");

            migrationBuilder.RenameColumn(
                name: "EvidenceSegmentOrder",
                table: "StoryEntities",
                newName: "FirstSeenOrder");
        }
    }
}
