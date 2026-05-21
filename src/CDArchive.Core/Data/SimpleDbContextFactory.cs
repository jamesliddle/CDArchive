using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Data;

/// <summary>
/// Minimal <see cref="IDbContextFactory{TContext}"/> for direct construction
/// of <see cref="CanonDbContext"/> outside the DI container — used by the
/// SeedDb tool and the test suite when they build a one-off
/// <see cref="Services.SqliteCanonDataService"/> against a temp-file SQLite
/// database.
///
/// <para>Production runtime uses EF Core's own pooled factory registered via
/// <c>AddDbContextFactory&lt;CanonDbContext&gt;()</c>; this type is not used
/// there.</para>
/// </summary>
public sealed class SimpleDbContextFactory : IDbContextFactory<CanonDbContext>
{
    private readonly DbContextOptions<CanonDbContext> _options;
    public SimpleDbContextFactory(DbContextOptions<CanonDbContext> options) => _options = options;
    public CanonDbContext CreateDbContext() => new(_options);
}
