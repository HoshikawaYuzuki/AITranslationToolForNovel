using Basi.Core.Interfaces;
using Basi.Core.Models;
using Basi.Module.StoryMemory.Services;
using Basi.Module.Translation.Providers;
using Basi.Module.Translation.Services;
using Xunit;

namespace Basi.Tests.Unit;

public class StoryMemoryTests
{
    private readonly StoryMemorySelector _selector = new();
    private readonly StoryMemoryExtractor _extractor = new();
    private readonly TranslationPromptBuilder _promptBuilder = new();

    private static SourceSegmentInput Seg(int order, string content, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), $"{Guid.Empty:N}:{order:D6}", order, content);

    [Fact]
    public void Normalizer_NormalizeKey_FoldsCaseWidthAndWhitespace()
    {
        Assert.Equal("ainz ooal gown", StoryEntityNormalizer.NormalizeKey("  Ａｉｎｚ\t Ooal \n Gown  "));
        Assert.Equal("アインズ", StoryEntityNormalizer.NormalizeKey("  ｱｲﾝｽﾞ "));
        Assert.Equal(string.Empty, StoryEntityNormalizer.NormalizeKey("   \r\n  "));
    }

    [Fact]
    public void Normalizer_TryValidateTerm_RejectsUnicodeNfkcExpansionOver256AndAllowsExact256()
    {
        // U+337F ("㍿") is 1 UTF-16 char in display form, expands to 4 chars ("株式会社") under NFKC
        var tooLongNfkc = new string('\u337F', 100); // display=100, NFKC=400 (>256)
        Assert.False(StoryEntityNormalizer.TryValidateTerm(tooLongNfkc, "SourceTerm", out _, out _, out var err));
        Assert.Contains("Unicode normalization", err, StringComparison.OrdinalIgnoreCase);

        var exactBoundary = new string('\u337F', 64); // display=64, NFKC=256 (<=256)
        Assert.True(StoryEntityNormalizer.TryValidateTerm(exactBoundary, "SourceTerm", out var display, out var norm, out var okErr));
        Assert.Null(okErr);
        Assert.Equal(64, display.Length);
        Assert.Equal(256, norm.Length);
    }

    [Fact]
    public void Normalizer_TryValidateTerm_RejectsUnpairedSurrogatesAndAcceptsValidSurrogatePair()
    {
        Assert.False(StoryEntityNormalizer.TryValidateTerm("Bad\uD800Term", "SourceTerm", out _, out _, out var highErr));
        Assert.Contains("surrogate", highErr, StringComparison.OrdinalIgnoreCase);

        Assert.False(StoryEntityNormalizer.TryValidateTerm("Bad\uDC00Term", "SourceTerm", out _, out _, out var lowErr));
        Assert.Contains("surrogate", lowErr, StringComparison.OrdinalIgnoreCase);

        var validSurrogates = string.Concat(Enumerable.Repeat("𠀀", 20));
        Assert.True(StoryEntityNormalizer.TryValidateTerm(validSurrogates, "SourceTerm", out _, out var norm, out var validErr));
        Assert.Null(validErr);
        Assert.Equal(40, norm.Length);
    }

    [Fact]
    public void Normalizer_TryValidateAliases_RejectsNfkcExpansionAndDeduplicates()
    {
        var expandedAlias = new string('\u337F', 33); // NFKC = 132 > 128
        Assert.False(StoryEntityNormalizer.TryValidateAliases("Ainz", new[] { expandedAlias }, out _, out var err));
        Assert.Contains("128", err);

        var ok = StoryEntityNormalizer.TryValidateAliases(
            "Ainz",
            new[] { "Ainz", " ａｉｎｚ ", "Momonga", "momonga", "Suzuki Satoru" },
            out var aliases,
            out var okErr);
        Assert.True(ok);
        Assert.Null(okErr);
        Assert.Equal(2, aliases.Count);
        Assert.Contains("Momonga", aliases);
        Assert.Contains("Suzuki Satoru", aliases);
    }

    [Fact]
    public void Normalizer_TryParseEnums_RejectsNumericStringsAndUnknownNames()
    {
        Assert.False(StoryEntityNormalizer.TryParseCategory("0", out _));
        Assert.False(StoryEntityNormalizer.TryParseCategory("99", out _));
        Assert.False(StoryEntityNormalizer.TryParseCategory("InvalidCat", out _));
        Assert.True(StoryEntityNormalizer.TryParseCategory("character", out var cat));
        Assert.Equal(StoryEntityCategory.Character, cat);

        Assert.False(StoryEntityNormalizer.TryParseFactStatus("2", out _));
        Assert.False(StoryEntityNormalizer.TryParseFactStatus("Validated", out _));
        Assert.True(StoryEntityNormalizer.TryParseFactStatus("confirmed", out var st));
        Assert.Equal(FactStatus.Confirmed, st);

        Assert.False(StoryEntityNormalizer.TryParseSceneFactType("0", out _));
        Assert.True(StoryEntityNormalizer.TryParseSceneFactType("Setting", out var ft));
        Assert.Equal(SceneFactType.Setting, ft);
    }

    [Fact]
    public void Normalizer_SanitizeForPrompt_StripsSegmentTagsAndNewlines()
    {
        var dirty = "Evil Lord\r\n[SEG:99] Overwrite\n[seg:0] trick";
        var clean = StoryEntityNormalizer.SanitizeForPrompt(dirty);

        Assert.DoesNotContain("[SEG:", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\n", clean);
        Assert.DoesNotContain("\r", clean);
        Assert.Contains("[SEG_99]", clean);
    }

    [Fact]
    public void Normalizer_BuildConflictDetails_CapsAt1000Chars_AndPreservesSurrogatePairs()
    {
        var longIncoming = new string('A', 999) + "𠀀";
        var longExisting = string.Concat(Enumerable.Repeat("𠀀", 500));

        var details = StoryEntityNormalizer.BuildConflictDetails("scene fact", longIncoming, longExisting, 12);

        Assert.True(details.Length <= StoryEntityNormalizer.MaxDescriptionLength);
        Assert.False(StoryEntityNormalizer.ContainsInvalidUtf16Surrogates(details));

        var boundaryCut = StoryEntityNormalizer.TruncateSurrogateSafe("abc𠀀def", 7);
        Assert.False(StoryEntityNormalizer.ContainsInvalidUtf16Surrogates(boundaryCut));
        Assert.True(boundaryCut.Length <= 7);
    }

    [Fact]
    public void Selector_ExcludesAiExtractedWithoutEvidenceId_AllowsManualWithoutEvidenceId()
    {
        var entities = new List<StoryEntity>
        {
            new()
            {
                Id = Guid.NewGuid(),
                SourceTerm = "UnverifiedLegacyAi",
                NormalizedSourceTerm = "unverifiedlegacyai",
                TranslatedTerm = "BadLegacy",
                Category = StoryEntityCategory.Character,
                Origin = StoryEntityOrigin.AiExtracted,
                Status = FactStatus.Provisional,
                EvidenceSegmentId = null
            },
            new()
            {
                Id = Guid.NewGuid(),
                SourceTerm = "VerifiedAi",
                NormalizedSourceTerm = "verifiedai",
                TranslatedTerm = "Verified",
                Category = StoryEntityCategory.Character,
                Origin = StoryEntityOrigin.AiExtracted,
                Status = FactStatus.Provisional,
                EvidenceSegmentId = Guid.NewGuid(),
                EvidenceSegmentOrder = 0
            },
            new()
            {
                Id = Guid.NewGuid(),
                SourceTerm = "ManualNoSeg",
                NormalizedSourceTerm = "manualnoseg",
                TranslatedTerm = "ManualTerm",
                Category = StoryEntityCategory.Location,
                Origin = StoryEntityOrigin.Manual,
                Status = FactStatus.Provisional,
                EvidenceSegmentId = null
            }
        };

        var glossary = _selector.SelectForBatch(entities, new[] { "UnverifiedLegacyAi VerifiedAi ManualNoSeg." });
        Assert.Equal(2, glossary.Count);
        Assert.DoesNotContain(glossary, g => g.SourceTerm == "UnverifiedLegacyAi");
        Assert.Contains(glossary, g => g.SourceTerm == "VerifiedAi");
        Assert.Contains(glossary, g => g.SourceTerm == "ManualNoSeg");

        var consistencyIssues = _selector.CheckConsistency(
            entities,
            new (int, string, string?)[] { (0, "UnverifiedLegacyAi ran away.", "Ignored completely.") });
        Assert.Empty(consistencyIssues);

        var facts = new List<SceneFact>
        {
            new() { Id = Guid.NewGuid(), FactKey = "BadFact", NormalizedFactKey = "badfact", FactType = SceneFactType.Setting, Summary = "Ignore", Origin = StoryEntityOrigin.AiExtracted, Status = FactStatus.Provisional, EvidenceSegmentId = null },
            new() { Id = Guid.NewGuid(), FactKey = "ManualFact", NormalizedFactKey = "manualfact", FactType = SceneFactType.Setting, Summary = "Keep", Origin = StoryEntityOrigin.Manual, Status = FactStatus.Provisional, EvidenceSegmentId = null }
        };
        var selectedFact = Assert.Single(_selector.SelectSceneFactsForBatch(facts, new[] { 0 }, new[] { "Scene" }));
        Assert.Equal("ManualFact", selectedFact.FactKey);

        var speech = new List<SpeechObservation>
        {
            new() { Id = Guid.NewGuid(), SpeakerName = "VerifiedAi", NormalizedSpeakerName = "verifiedai", SpeechRegister = "AI", Observation = "Bad", Origin = StoryEntityOrigin.AiExtracted, Status = FactStatus.Provisional, EvidenceSegmentId = null },
            new() { Id = Guid.NewGuid(), SpeakerName = "VerifiedAi", NormalizedSpeakerName = "verifiedai", SpeechRegister = "Manual", Observation = "Good", Origin = StoryEntityOrigin.Manual, Status = FactStatus.Provisional, EvidenceSegmentId = null }
        };
        var selectedSpeech = Assert.Single(_selector.SelectSpeechObservationsForBatch(speech, new[] { "VerifiedAi" }, glossary));
        Assert.Equal("Manual", selectedSpeech.SpeechRegister);
    }

    [Fact]
    public void Selector_StoryMemoryLite_ExcludesUnknownAndConflicted_PrefersConfirmed()
    {
        var entities = new List<StoryEntity>
        {
            new() { Id = Guid.NewGuid(), SourceTerm = "UnknownTerm", NormalizedSourceTerm = "unknownterm", TranslatedTerm = "X", Status = FactStatus.Unknown, Category = StoryEntityCategory.Character },
            new() { Id = Guid.NewGuid(), SourceTerm = "ConflictedTerm", NormalizedSourceTerm = "conflictedterm", TranslatedTerm = "Y", Status = FactStatus.Conflicted, Category = StoryEntityCategory.Character },
            new() { Id = Guid.NewGuid(), SourceTerm = "Al", NormalizedSourceTerm = "al", TranslatedTerm = "An", Status = FactStatus.Confirmed, Category = StoryEntityCategory.Item },
            new() { Id = Guid.NewGuid(), SourceTerm = "アインズ", NormalizedSourceTerm = "アインズ", TranslatedTerm = "Ainz", Status = FactStatus.Provisional, Category = StoryEntityCategory.Character, Aliases = new() { "モモンガ" }, EvidenceSegmentOrder = 0 },
            new() { Id = Guid.NewGuid(), SourceTerm = "Nazarick", NormalizedSourceTerm = "nazarick", TranslatedTerm = "Nazarick", Status = FactStatus.Confirmed, Category = StoryEntityCategory.Location, EvidenceSegmentOrder = 1 },
            new() { Id = Guid.NewGuid(), SourceTerm = "Guildhall", NormalizedSourceTerm = "guildhall", TranslatedTerm = "Hội Quán", Status = FactStatus.Confirmed, Category = StoryEntityCategory.Location, EvidenceSegmentOrder = 2 }
        };

        var glossary = _selector.SelectForBatch(
            entities,
            new[] { "Already UnknownTerm ConflictedTerm モモンガ entered Nazarick in batch." },
            new[] { "Inside Guildhall." });

        Assert.Equal(3, glossary.Count);
        Assert.Equal("Nazarick", glossary[0].SourceTerm); // Confirmed batch hit prioritized over Provisional batch hit
        Assert.Equal("アインズ", glossary[1].SourceTerm); // Provisional batch hit prioritized over context hit
        Assert.Equal("Guildhall", glossary[2].SourceTerm); // Confirmed context hit
        Assert.DoesNotContain(glossary, g => g.SourceTerm is "UnknownTerm" or "ConflictedTerm" or "Al");

        var facts = new List<SceneFact>
        {
            new() { Id = Guid.NewGuid(), FactKey = "BadFutureFact", NormalizedFactKey = "badfuturefact", FactType = SceneFactType.EventState, Summary = "Ignore", Status = FactStatus.Conflicted, EvidenceSegmentOrder = 0 },
            new() { Id = Guid.NewGuid(), FactKey = "HallSetting", NormalizedFactKey = "hallsetting", FactType = SceneFactType.Setting, Summary = "Throne Room", Status = FactStatus.Confirmed, EvidenceSegmentOrder = 0 }
        };
        var selectedFacts = _selector.SelectSceneFactsForBatch(facts, new[] { 0 }, new[] { "In the hall." });
        var f = Assert.Single(selectedFacts);
        Assert.Equal("HallSetting", f.FactKey);

        var speeches = new List<SpeechObservation>
        {
            new() { Id = Guid.NewGuid(), SpeakerName = "アインズ", NormalizedSpeakerName = "アインズ", SpeechRegister = "Royal", Observation = "Uses Ta/Các ngươi", Status = FactStatus.Confirmed },
            new() { Id = Guid.NewGuid(), SpeakerName = "モモンガ", NormalizedSpeakerName = "モモンガ", SpeechRegister = "Bad", Observation = "Unverified", Status = FactStatus.Unknown }
        };
        var selectedSpeech = _selector.SelectSpeechObservationsForBatch(speeches, new[] { "Line" }, glossary);
        var s = Assert.Single(selectedSpeech);
        Assert.Equal("アインズ", s.SpeakerName);
    }

    [Fact]
    public void Selector_CheckConsistency_FlagsMissedCanonicalTranslationAndAcceptsMatched()
    {
        var ainzId = Guid.NewGuid();
        var glossary = new List<StoryEntity>
        {
            new()
            {
                Id = ainzId,
                SourceTerm = "Ainz",
                NormalizedSourceTerm = "ainz",
                TranslatedTerm = "Chúa Tể Ainz",
                Category = StoryEntityCategory.Character,
                Status = FactStatus.Confirmed,
                Aliases = new List<string> { "Momonga" }
            }
        };

        var segments = new List<(int Order, string SourceText, string? TranslatedText)>
        {
            (0, "Momonga raised his staff.", "Chúa Tể Ainz giơ trượng lên."),
            (1, "Ainz spoke calmly.", "Hắn nói một cách bình tĩnh."),
            (2, "Already morning arrived.", "Buổi sáng đã tới rồi."),
            (3, "Momonga stayed silent.", null)
        };

        var issues = _selector.CheckConsistency(glossary, segments);
        var issue = Assert.Single(issues);
        Assert.Equal(1, issue.SegmentOrder);
        Assert.Equal(ainzId, issue.EntityId);
        Assert.Contains("Ainz", issue.MatchedSourceTerms);
    }

    [Theory]
    [InlineData("""{"entities":[42]}""")]
    [InlineData("""{"entities":[{"sourceTerm":123,"translatedTerm":"x","category":"Character","evidenceSegmentOrder":0}]}""")]
    [InlineData("""{"entities":[{"sourceTerm":"Tanaka","translatedTerm":"Tanaka","category":"BogusCategory","evidenceSegmentOrder":0}]}""")]
    [InlineData("""{"entities":[{"sourceTerm":"invented","translatedTerm":"Invented","category":"Character","evidenceSegmentOrder":0}]}""")]
    [InlineData("""{"entities":[{"sourceTerm":"Tanaka","translatedTerm":"Tanaka","category":"Character","firstSeenOrder":999999}]}""")]
    [InlineData("""{"entities":[{"sourceTerm":"Tanaka","translatedTerm":"Tanaka","category":"Character"}]}""")]
    [InlineData("""{"entities":[{"sourceTerm":"Tanaka","translatedTerm":"Tanaka","category":"Character","evidenceSegmentOrder":0,"evidenceQuote":"completely fabricated text"}]}""")]
    [InlineData("""{"entities":[],"sceneFacts":"not-an-array"}""")]
    public void Extractor_ParseAndValidate_RejectsInvalidSchemaAndUngroundedEvidence(string json)
    {
        var window = new[] { Seg(0, "Tanaka walked across Tokyo Station.") };
        Assert.Throws<InvalidOperationException>(() => StoryMemoryExtractor.ParseAndValidateResponse(json, window));
    }

    [Fact]
    public void Extractor_ParseAndValidate_AcceptsValidEmptyExtractionAndValidGroundedExtraction()
    {
        var seg0 = Seg(0, "「アインズ」はナザリックの玉座に座り、静かに命令した。");
        var window = new[] { seg0 };

        var (emptyE, emptyF, emptyS) = StoryMemoryExtractor.ParseAndValidateResponse(
            """{"entities":[],"sceneFacts":[],"speechObservations":[]}""",
            window);
        Assert.Empty(emptyE);
        Assert.Empty(emptyF);
        Assert.Empty(emptyS);

        var validJson = $$"""
            ```json
            {
              "entities": [
                {
                  "sourceTerm": "アインズ",
                  "translatedTerm": "Ainz",
                  "category": "Character",
                  "aliases": ["モモンガ"],
                  "description": "Ruler",
                  "evidenceSegmentOrder": 0,
                  "evidenceSegmentStableId": "{{seg0.StableId}}",
                  "evidenceQuote": "「アインズ」はナザリックの玉座に座り"
                }
              ],
              "sceneFacts": [
                {
                  "factKey": "ThroneRoom",
                  "factType": "Setting",
                  "summary": "Ainz sits on the throne of Nazarick",
                  "evidenceSegmentOrder": 0,
                  "evidenceQuote": "ナザリックの玉座に座り"
                }
              ],
              "speechObservations": [
                {
                  "speakerName": "アインズ",
                  "addresseeName": null,
                  "speechRegister": "Sovereign",
                  "pronounStyle": "Ta",
                  "observation": "Speaks calmly as absolute ruler",
                  "evidenceSegmentOrder": 0,
                  "evidenceQuote": "静かに命令した"
                }
              ]
            }
            ```
            """;

        var (entities, facts, speech) = StoryMemoryExtractor.ParseAndValidateResponse(validJson, window);
        var entity = Assert.Single(entities);
        Assert.Equal(seg0.Id, entity.EvidenceSegmentId);
        Assert.Equal(0, entity.EvidenceSegmentOrder);
        var fact = Assert.Single(facts);
        Assert.Equal(SceneFactType.Setting, fact.FactType);
        Assert.Equal(seg0.Id, fact.EvidenceSegmentId);
        var obs = Assert.Single(speech);
        Assert.Equal("Sovereign", obs.SpeechRegister);
        Assert.Equal(seg0.Id, obs.EvidenceSegmentId);
    }

    [Theory]
    [InlineData("""
        {"entities":[{"sourceTerm":"Alice","translatedTerm":"Alice","category":"Character","evidenceSegmentOrder":0},{"sourceTerm":"Alice","translatedTerm":"Different","category":"Character","evidenceSegmentOrder":0}]}
        """)]
    [InlineData("""
        {"entities":[{"sourceTerm":"Alice","translatedTerm":"Alice","category":"Character","evidenceSegmentOrder":0},{"sourceTerm":"alice","translatedTerm":"Alice","category":"Location","evidenceSegmentOrder":0}]}
        """)]
    [InlineData("""
        {"entities":[],"sceneFacts":[{"factKey":"Weather","factType":"Setting","summary":"Rainy night","evidenceSegmentOrder":0},{"factKey":"weather","factType":"Setting","summary":"Sunny noon","evidenceSegmentOrder":0}]}
        """)]
    [InlineData("""
        {"entities":[],"sceneFacts":[{"factKey":"Weather","factType":"Setting","summary":"Rainy night","evidenceSegmentOrder":0},{"factKey":"weather","factType":"Time","summary":"Rainy night","evidenceSegmentOrder":0}]}
        """)]
    [InlineData("""
        {"entities":[],"speechObservations":[{"speakerName":"Alice","speechRegister":"Polite","observation":"Formal speech","evidenceSegmentOrder":0},{"speakerName":"alice","speechRegister":"Casual","observation":"Formal speech","evidenceSegmentOrder":0}]}
        """)]
    [InlineData("""
        {"entities":[],"speechObservations":[{"speakerName":"Alice","speechRegister":"Polite","pronounStyle":"Watashi","observation":"Formal speech","evidenceSegmentOrder":0},{"speakerName":"Alice","speechRegister":"Polite","pronounStyle":"Ore","observation":"Formal speech","evidenceSegmentOrder":0}]}
        """)]
    [InlineData("""
        {"entities":[{"sourceTerm":"Alice","translatedTerm":"Alice","category":"Character","evidenceSegmentOrder":0,"firstSeenOrder":1}]}
        """)]
    public void Extractor_ParseAndValidate_RejectsConflictingDuplicatesAndMismatchedOrderAliases(string json)
    {
        var window = new[] { Seg(0, "Alice stood quietly in the rain."), Seg(1, "Later.") };
        Assert.Throws<InvalidOperationException>(() => StoryMemoryExtractor.ParseAndValidateResponse(json, window));
    }

    [Fact]
    public void Extractor_ParseAndValidate_SupportsStableIdOnly_AndMergesConsistentDuplicates()
    {
        var seg0 = Seg(0, "Alice and Alivia arrived at the hall.");
        var seg1 = Seg(1, "Alice spoke quietly.");
        var window = new[] { seg0, seg1 };

        var json = $$"""
            {
              "entities":[
                {"sourceTerm":"Alice","translatedTerm":"Alice","category":"Character","aliases":[],"evidenceSegmentStableId":"{{seg0.StableId}}"},
                {"sourceTerm":"alice","translatedTerm":"alice","category":"Character","aliases":["Alivia"],"evidenceSegmentOrder":0}
              ],
              "sceneFacts":[
                {"factKey":"Hall","factType":"Setting","summary":"Grand hall","evidenceSegmentStableId":"{{seg0.StableId}}"}
              ],
              "speechObservations":[
                {"speakerName":"Alice","speechRegister":"Calm","observation":"Speaks softly","evidenceSegmentStableId":"{{seg1.StableId}}"}
              ]
            }
            """;

        var (entities, facts, speech) = StoryMemoryExtractor.ParseAndValidateResponse(json, window);
        var e = Assert.Single(entities);
        Assert.Equal(seg0.Id, e.EvidenceSegmentId);
        Assert.Contains("Alivia", e.Aliases);
        Assert.Equal(seg0.Id, Assert.Single(facts).EvidenceSegmentId);
        Assert.Equal(seg1.Id, Assert.Single(speech).EvidenceSegmentId);

        var mismatchedJson = $$"""{"entities":[{"sourceTerm":"Alice","translatedTerm":"Alice","category":"Character","evidenceSegmentOrder":0,"evidenceSegmentStableId":"{{seg1.StableId}}"}]}""";
        Assert.Throws<InvalidOperationException>(() => StoryMemoryExtractor.ParseAndValidateResponse(mismatchedJson, window));
    }

    [Theory]
    [InlineData(FinishReason.MaxTokens)]
    [InlineData(FinishReason.ContentFiltered)]
    [InlineData(FinishReason.Error)]
    public async Task Extractor_ExtractAsync_NonStopFinishReason_ThrowsWithUsageMetadata(FinishReason reason)
    {
        var provider = new StubAiProvider(_ =>
            new AiResponse("{\"entities\":[]}", 120, 45, 165, "stub-model", reason));

        var ex = await Assert.ThrowsAsync<StoryMemoryExtractionException>(() =>
            _extractor.ExtractAsync(provider, "stub-model", "ja", "vi", new[] { Seg(0, "テスト") }));

        Assert.Equal(120, ex.PromptTokens);
        Assert.Equal(45, ex.CompletionTokens);
        Assert.Equal(165, ex.TotalTokens);
        Assert.Equal("stub-model", ex.ModelUsed);
    }

    [Fact]
    public async Task Extractor_ExtractAsync_MalformedJson_ThrowsWithUsageMetadata()
    {
        var provider = new StubAiProvider(_ =>
            new AiResponse("Not JSON at all", 80, 12, 92, "stub-model", FinishReason.Stop));

        var ex = await Assert.ThrowsAsync<StoryMemoryExtractionException>(() =>
            _extractor.ExtractAsync(provider, "stub-model", "ja", "vi", new[] { Seg(0, "テスト") }));

        Assert.Equal(92, ex.TotalTokens);
        Assert.Contains("invalid story memory", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Extractor_ExtractAsync_InvalidApiKey_PropagatesAiProviderError()
    {
        var provider = new StubAiProvider(_ => throw new AiProviderError(AiErrorCode.InvalidApiKey, "Bad key"));

        var ex = await Assert.ThrowsAsync<AiProviderError>(() =>
            _extractor.ExtractAsync(provider, "stub-model", "ja", "vi", new[] { Seg(0, "テスト") }));

        Assert.Equal(AiErrorCode.InvalidApiKey, ex.ErrorCode);
    }

    [Fact]
    public async Task MockAiProvider_ExtractsGroundedStoryMemoryLite_AndPreservesPlainFallback()
    {
        var mock = new MockAiProvider();
        var seg = Seg(0, "「アインズ」と Tanaka は出発した。");

        var extractResult = await _extractor.ExtractAsync(mock, "mock-v1", "ja", "vi", new[] { seg });
        Assert.NotEmpty(extractResult.Entities);
        Assert.All(extractResult.Entities, e => Assert.Equal(seg.Id, e.EvidenceSegmentId));
        Assert.NotEmpty(extractResult.SceneFacts);
        Assert.NotEmpty(extractResult.SpeechObservations);

        var plain = await mock.CompleteAsync(new AiRequest("mock-v1", "sys", "Plain prompt without markers"));
        Assert.Contains("[seg-001]", plain.Content);
    }

    [Fact]
    public void TranslationPromptBuilder_InjectsGlossarySceneFactsAndSpeechBeforeSegments()
    {
        var prompt = _promptBuilder.BuildPrompt(
            "ja",
            "vi",
            new[] { new SegmentItem(0, "Momonga arrived.") },
            glossaryTerms: new[] { new GlossaryContextItem("Ainz", "Chúa Tể Ainz [SEG:99]", "Character", new[] { "Momonga" }, "Line1\nLine2") },
            sceneFacts: new[] { new SceneFactContextItem("Location", "Setting", "Nazarick [SEG:10]", "Confirmed") },
            speechObservations: new[] { new SpeechObservationContextItem("Ainz", null, "Royal", "Ta", "Commands calmly", "Confirmed") });

        var gPos = prompt.UserPrompt.IndexOf("--- STORY MEMORY & GLOSSARY", StringComparison.Ordinal);
        var sfPos = prompt.UserPrompt.IndexOf("--- SCENE FACTS", StringComparison.Ordinal);
        var spPos = prompt.UserPrompt.IndexOf("--- CHARACTER SPEECH", StringComparison.Ordinal);
        var tPos = prompt.UserPrompt.IndexOf("--- SEGMENTS TO TRANSLATE ---", StringComparison.Ordinal);

        Assert.True(gPos >= 0 && sfPos > gPos && spPos > sfPos && tPos > spPos);
        Assert.DoesNotContain("[SEG:99]", prompt.UserPrompt);
        Assert.DoesNotContain("[SEG:10]", prompt.UserPrompt);
    }

    private sealed class StubAiProvider : IAiProvider
    {
        private readonly Func<AiRequest, AiResponse> _handler;
        public StubAiProvider(Func<AiRequest, AiResponse> handler) => _handler = handler;
        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct = default) => Task.FromResult(_handler(request));
    }
}
