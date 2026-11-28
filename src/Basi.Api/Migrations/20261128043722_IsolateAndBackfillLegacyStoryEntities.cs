using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basi.Api.Migrations
{
    /// <inheritdoc />
    public partial class IsolateAndBackfillLegacyStoryEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider != null && ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                // 1. Verify and backfill EvidenceSegmentId / EvidenceSegmentStableId for legacy AiExtracted entities
                // only when the referenced Segment in the same novel and order genuinely contains the term or an alias.
                migrationBuilder.Sql("""
                    WITH candidate_matches AS (
                        SELECT DISTINCT ON (e."Id")
                            e."Id" AS entity_id,
                            s."Id" AS seg_id,
                            s."StableId" AS seg_stable_id
                        FROM "StoryEntities" e
                        JOIN "Segments" s
                          ON s."NovelId" = e."NovelId"
                         AND s."Order" = e."EvidenceSegmentOrder"
                        CROSS JOIN LATERAL (
                            SELECT regexp_replace(lower(normalize(s."Content", NFKC)), '\s+', ' ', 'g') AS norm_content
                        ) seg_norm
                        WHERE e."Origin" = 'AiExtracted'
                          AND e."EvidenceSegmentId" IS NULL
                          AND e."EvidenceSegmentOrder" IS NOT NULL
                          AND EXISTS (
                              SELECT 1
                              FROM (
                                  SELECT e."NormalizedSourceTerm" AS term_key
                                  UNION ALL
                                  SELECT regexp_replace(lower(normalize(a.val, NFKC)), '\s+', ' ', 'g')
                                  FROM jsonb_array_elements_text(COALESCE(e."Aliases", '[]'::jsonb)) AS a(val)
                              ) terms
                              WHERE terms.term_key <> ''
                                AND (
                                    CASE
                                        WHEN terms.term_key ~ '^[a-z0-9]([[:ascii:]]*[a-z0-9])?$'
                                            THEN seg_norm.norm_content ~ ('(^|[^a-z0-9])' || regexp_replace(terms.term_key, '([]\.*+?^${}()|[])', '\\\1', 'g') || '([^a-z0-9]|$)')
                                        ELSE strpos(seg_norm.norm_content, terms.term_key) > 0
                                    END
                                )
                          )
                        ORDER BY e."Id", s."Id"
                    )
                    UPDATE "StoryEntities" e
                    SET "EvidenceSegmentId" = m.seg_id,
                        "EvidenceSegmentStableId" = m.seg_stable_id
                    FROM candidate_matches m
                    WHERE e."Id" = m.entity_id;
                    """);

                // 2. Isolate unverified legacy AI-extracted records (missing/fake segment order or non-matching source)
                // by moving them to Unknown and clearing synthetic/unverified segment order references.
                migrationBuilder.Sql("""
                    UPDATE "StoryEntities"
                    SET "Status" = 'Unknown',
                        "EvidenceSegmentOrder" = NULL,
                        "EvidenceSegmentStableId" = NULL
                    WHERE "Origin" = 'AiExtracted'
                      AND "EvidenceSegmentId" IS NULL
                      AND "IsLocked" = FALSE
                      AND "Status" <> 'Confirmed';

                    UPDATE "SceneFacts"
                    SET "Status" = 'Unknown',
                        "EvidenceSegmentOrder" = NULL,
                        "EvidenceSegmentStableId" = NULL
                    WHERE "Origin" = 'AiExtracted'
                      AND "EvidenceSegmentId" IS NULL
                      AND "IsLocked" = FALSE
                      AND "Status" <> 'Confirmed';

                    UPDATE "SpeechObservations"
                    SET "Status" = 'Unknown',
                        "EvidenceSegmentOrder" = NULL,
                        "EvidenceSegmentStableId" = NULL
                    WHERE "Origin" = 'AiExtracted'
                      AND "EvidenceSegmentId" IS NULL
                      AND "IsLocked" = FALSE
                      AND "Status" <> 'Confirmed';
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One-way legacy evidence verification and quarantine cannot be safely reversed without reintroducing unverified data.
        }
    }
}
