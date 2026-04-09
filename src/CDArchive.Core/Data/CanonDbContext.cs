using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Data;

/// <summary>
/// EF Core DbContext for the Classical Canon SQLite database.
/// </summary>
public class CanonDbContext : DbContext
{
    public DbSet<PieceRow> Pieces { get; set; } = null!;
    public DbSet<ComposerRow> Composers { get; set; } = null!;
    public DbSet<SettingRow> Settings { get; set; } = null!;

    private readonly string _dbPath;

    public CanonDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite($"Data Source={_dbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PieceRow>(e =>
        {
            e.HasIndex(p => p.Composer);
            e.HasIndex(p => p.InstrumentationCategory);
            e.HasIndex(p => new { p.Composer, p.CatalogSortPrefix, p.CatalogSortNumber, p.CatalogSortSuffix });
        });

        modelBuilder.Entity<ComposerRow>(e =>
        {
            e.HasIndex(c => c.SortName);
        });
    }
}
