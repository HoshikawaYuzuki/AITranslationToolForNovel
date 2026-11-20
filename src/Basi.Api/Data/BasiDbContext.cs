using Basi.Core.Models;
using Basi.Module.Identity.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Basi.Api.Data;

public class BasiDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public BasiDbContext(DbContextOptions<BasiDbContext> options) : base(options)
    {
    }

    public DbSet<Novel> Novels => Set<Novel>();
    public DbSet<Segment> Segments => Set<Segment>();

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
    }
}
