using System.Text.Json;
using Basi.Core.Models;
using Basi.Module.Identity.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Basi.Api.Data;

public class BasiDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public BasiDbContext(DbContextOptions<BasiDbContext> options) : base(options)
    {
    }

    public DbSet<Novel> Novels => Set<Novel>();
    public DbSet<Segment> Segments => Set<Segment>();
    public DbSet<TranslationJob> TranslationJobs => Set<TranslationJob>();
    public DbSet<TranslatedSegment> TranslatedSegments => Set<TranslatedSegment>();
    public DbSet<StoryEntity> StoryEntities => Set<StoryEntity>();
    public DbSet<SceneFact> SceneFacts => Set<SceneFact>();
    public DbSet<SpeechObservation> SpeechObservations => Set<SpeechObservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ---- ApplicationUser ----
        modelBuilder.Entity<ApplicationUser>(b =>
        {
            b.Property(u => u.DisplayName).HasMaxLength(256);
            b.Property(u => u.CreatedAt).HasDefaultValueSql("now()");
        });

        // ---- Novel ----
        modelBuilder.Entity<Novel>(b =>
        {
            b.HasKey(n => n.Id);
            b.Property(n => n.Title).HasMaxLength(512).IsRequired();
            b.Property(n => n.SourceLanguage).HasMaxLength(32).IsRequired();
            b.Property(n => n.RawContent).IsRequired();
            b.Property(n => n.UploadedAt).HasDefaultValueSql("now()");
            b.HasOne<ApplicationUser>()
             .WithMany()
             .HasForeignKey(n => n.OwnerId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ---- Segment ----
        modelBuilder.Entity<Segment>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.StableId).HasMaxLength(64).IsRequired();
            b.HasIndex(s => s.StableId).IsUnique();
            b.Property(s => s.Content).IsRequired();
            b.HasOne(s => s.Novel)
             .WithMany(n => n.Segments)
             .HasForeignKey(s => s.NovelId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ---- TranslationJob ----
        modelBuilder.Entity<TranslationJob>(b =>
        {
            b.HasKey(j => j.Id);
            b.Property(j => j.TargetLanguage).HasMaxLength(32).IsRequired();
            b.Property(j => j.Provider).HasMaxLength(64).IsRequired();
            b.Property(j => j.Model).HasMaxLength(128).IsRequired();
            b.Property(j => j.ActualModel).HasMaxLength(128);
            b.Property(j => j.RequestCount).HasDefaultValue(0);
            b.Property(j => j.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(j => j.PromptTokens).HasDefaultValue(0);
            b.Property(j => j.CompletionTokens).HasDefaultValue(0);
            b.Property(j => j.TotalTokens).HasDefaultValue(0);
            b.Property(j => j.CreatedAt).HasDefaultValueSql("now()");
            b.Property(j => j.UpdatedAt).HasDefaultValueSql("now()");
            b.HasIndex(j => j.OwnerId);
            b.HasIndex(j => j.Status);
            b.HasOne<ApplicationUser>()
             .WithMany()
             .HasForeignKey(j => j.OwnerId)
             .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(j => j.Novel)
             .WithMany()
             .HasForeignKey(j => j.NovelId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ---- TranslatedSegment ----
        modelBuilder.Entity<TranslatedSegment>(b =>
        {
            b.HasKey(ts => ts.Id);
            b.Property(ts => ts.SourceText).IsRequired();
            b.Property(ts => ts.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(ts => ts.UpdatedAt).HasDefaultValueSql("now()");
            b.HasIndex(ts => new { ts.JobId, ts.Order });
            b.HasOne(ts => ts.Job)
             .WithMany(j => j.TranslatedSegments)
             .HasForeignKey(ts => ts.JobId)
             .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(ts => ts.Segment)
             .WithMany()
             .HasForeignKey(ts => ts.SegmentId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ---- StoryEntity (BASI-004: ADR-002 JSONB Aliases + Per-Owner Scoping) ----
        var stringListConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => string.IsNullOrWhiteSpace(v)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());

        var stringListComparer = new ValueComparer<List<string>>(
            (c1, c2) => (c1 ?? new List<string>()).SequenceEqual(c2 ?? new List<string>(), StringComparer.Ordinal),
            c => c.Aggregate(0, (acc, item) => HashCode.Combine(acc, StringComparer.Ordinal.GetHashCode(item))),
            c => c.ToList());

        modelBuilder.Entity<StoryEntity>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.TargetLanguage).HasMaxLength(32).IsRequired();
            b.Property(e => e.SourceTerm).HasMaxLength(256).IsRequired();
            b.Property(e => e.NormalizedSourceTerm).HasMaxLength(256).IsRequired();
            b.Property(e => e.TranslatedTerm).HasMaxLength(256).IsRequired();
            b.Property(e => e.Category).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(e => e.Origin).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(e => e.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(e => e.ConflictDetails).HasMaxLength(1000);
            b.Property(e => e.Description).HasMaxLength(1000);
            b.Property(e => e.EvidenceSegmentStableId).HasMaxLength(64);
            b.Property(e => e.EvidenceQuote).HasMaxLength(500);
            b.Property(e => e.Version).IsConcurrencyToken().HasDefaultValue(1).IsRequired();
            b.Property(e => e.Aliases)
             .HasConversion(stringListConverter, stringListComparer)
             .HasColumnType("jsonb");
            b.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            b.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            b.HasIndex(e => new { e.NovelId, e.TargetLanguage, e.NormalizedSourceTerm }).IsUnique();
            b.HasIndex(e => e.OwnerId);
            b.HasOne<ApplicationUser>()
             .WithMany()
             .HasForeignKey(e => e.OwnerId)
             .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(e => e.Novel)
             .WithMany(n => n.StoryEntities)
             .HasForeignKey(e => e.NovelId)
             .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(e => e.EvidenceSegment)
             .WithMany()
             .HasForeignKey(e => e.EvidenceSegmentId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // ---- SceneFact (BASI-004 Story Memory Lite) ----
        modelBuilder.Entity<SceneFact>(b =>
        {
            b.HasKey(f => f.Id);
            b.Property(f => f.TargetLanguage).HasMaxLength(32).IsRequired();
            b.Property(f => f.FactKey).HasMaxLength(256).IsRequired();
            b.Property(f => f.NormalizedFactKey).HasMaxLength(256).IsRequired();
            b.Property(f => f.FactType).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(f => f.Summary).HasMaxLength(1000).IsRequired();
            b.Property(f => f.Origin).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(f => f.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(f => f.ConflictDetails).HasMaxLength(1000);
            b.Property(f => f.EvidenceSegmentStableId).HasMaxLength(64);
            b.Property(f => f.EvidenceQuote).HasMaxLength(500);
            b.Property(f => f.Version).IsConcurrencyToken().HasDefaultValue(1).IsRequired();
            b.Property(f => f.CreatedAt).HasDefaultValueSql("now()");
            b.Property(f => f.UpdatedAt).HasDefaultValueSql("now()");
            b.HasIndex(f => new { f.NovelId, f.TargetLanguage, f.NormalizedFactKey }).IsUnique();
            b.HasIndex(f => f.OwnerId);
            b.HasOne<ApplicationUser>()
             .WithMany()
             .HasForeignKey(f => f.OwnerId)
             .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(f => f.Novel)
             .WithMany(n => n.SceneFacts)
             .HasForeignKey(f => f.NovelId)
             .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(f => f.EvidenceSegment)
             .WithMany()
             .HasForeignKey(f => f.EvidenceSegmentId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // ---- SpeechObservation (BASI-004 Story Memory Lite) ----
        modelBuilder.Entity<SpeechObservation>(b =>
        {
            b.HasKey(o => o.Id);
            b.Property(o => o.TargetLanguage).HasMaxLength(32).IsRequired();
            b.Property(o => o.SpeakerName).HasMaxLength(256).IsRequired();
            b.Property(o => o.NormalizedSpeakerName).HasMaxLength(256).IsRequired();
            b.Property(o => o.AddresseeName).HasMaxLength(256);
            b.Property(o => o.NormalizedAddresseeName).HasMaxLength(256).IsRequired();
            b.Property(o => o.SpeechRegister).HasMaxLength(128).IsRequired();
            b.Property(o => o.PronounStyle).HasMaxLength(256);
            b.Property(o => o.Observation).HasMaxLength(1000).IsRequired();
            b.Property(o => o.Origin).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(o => o.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(o => o.ConflictDetails).HasMaxLength(1000);
            b.Property(o => o.EvidenceSegmentStableId).HasMaxLength(64);
            b.Property(o => o.EvidenceQuote).HasMaxLength(500);
            b.Property(o => o.Version).IsConcurrencyToken().HasDefaultValue(1).IsRequired();
            b.Property(o => o.CreatedAt).HasDefaultValueSql("now()");
            b.Property(o => o.UpdatedAt).HasDefaultValueSql("now()");
            b.HasIndex(o => new { o.NovelId, o.TargetLanguage, o.NormalizedSpeakerName, o.NormalizedAddresseeName }).IsUnique();
            b.HasIndex(o => o.OwnerId);
            b.HasOne<ApplicationUser>()
             .WithMany()
             .HasForeignKey(o => o.OwnerId)
             .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(o => o.Novel)
             .WithMany(n => n.SpeechObservations)
             .HasForeignKey(o => o.NovelId)
             .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(o => o.EvidenceSegment)
             .WithMany()
             .HasForeignKey(o => o.EvidenceSegmentId)
             .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
