using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basi.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTranslationJobAndSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TranslationJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    NovelId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetLanguage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TotalSegments = table.Column<int>(type: "integer", nullable: false),
                    CompletedSegments = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TranslationJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TranslationJobs_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TranslationJobs_Novels_NovelId",
                        column: x => x.NovelId,
                        principalTable: "Novels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TranslatedSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    SegmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    SourceText = table.Column<string>(type: "text", nullable: false),
                    TranslatedText = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TranslatedSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TranslatedSegments_Segments_SegmentId",
                        column: x => x.SegmentId,
                        principalTable: "Segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TranslatedSegments_TranslationJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "TranslationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TranslatedSegments_JobId_Order",
                table: "TranslatedSegments",
                columns: new[] { "JobId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_TranslatedSegments_SegmentId",
                table: "TranslatedSegments",
                column: "SegmentId");

            migrationBuilder.CreateIndex(
                name: "IX_TranslationJobs_NovelId",
                table: "TranslationJobs",
                column: "NovelId");

            migrationBuilder.CreateIndex(
                name: "IX_TranslationJobs_OwnerId",
                table: "TranslationJobs",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_TranslationJobs_Status",
                table: "TranslationJobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TranslatedSegments");

            migrationBuilder.DropTable(
                name: "TranslationJobs");
        }
    }
}
