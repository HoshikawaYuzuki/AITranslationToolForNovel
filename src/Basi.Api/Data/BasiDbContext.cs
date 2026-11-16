using Microsoft.EntityFrameworkCore;

namespace Basi.Api.Data;

public class BasiDbContext : DbContext
{
    public BasiDbContext(DbContextOptions<BasiDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Entity configurations will be added per module
    }
}
