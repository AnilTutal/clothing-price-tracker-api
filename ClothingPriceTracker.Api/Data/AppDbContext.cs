using ClothingPriceTracker.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ClothingPriceTracker.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<PriceRecord> PriceRecords => Set<PriceRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PriceRecord>(e =>
        {
            e.HasIndex(x => new { x.ProductId, x.RecordedAt });
            e.Property(x => x.Price).HasPrecision(18, 2);
        });
    }
}
