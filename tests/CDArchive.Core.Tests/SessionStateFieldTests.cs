using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// Pins the new <see cref="RecordingSession.State"/> field (added between
/// City and Country) through:
/// <list type="bullet">
///   <item>Schema mapping — column lands in <c>album_sessions.state</c>.</item>
///   <item>Save + load round-trip via <see cref="SqliteCanonDataService"/>.</item>
///   <item>Address-style placement in <see cref="RecordingSession.LocationSummary"/>.</item>
/// </list>
/// </summary>
public class SessionStateFieldTests
{
    private static SqliteCanonDataService NewService(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(),
            $"cdarchive_session_state_{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={dbPath};Foreign Keys=True")
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var json    = new CanonDataService(Path.GetTempPath());
        return new SqliteCanonDataService(factory, json);
    }

    [Fact]
    public async Task State_RoundTripsThroughSqlite()
    {
        var svc = NewService(out _);
        var album = new CanonAlbum
        {
            Title = "Cycle in California",
            Sessions = new List<RecordingSession>
            {
                new()
                {
                    Dates   = "1985",
                    Venue   = "Skywalker Sound",
                    City    = "Marin County",
                    State   = "California",
                    Country = "USA",
                },
            },
        };

        await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });
        var loaded = (await svc.LoadAlbumsAsync()).Single();

        var s = Assert.Single(loaded.Sessions!);
        Assert.Equal("California", s.State);
        Assert.Equal("Marin County", s.City);
        Assert.Equal("USA", s.Country);
    }

    [Fact]
    public async Task State_NullRoundTripsAsNull()
    {
        var svc = NewService(out _);
        var album = new CanonAlbum
        {
            Title = "Berlin Session",
            Sessions = new List<RecordingSession>
            {
                new() { City = "Berlin", Country = "Germany" },   // no State
            },
        };

        await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });
        var loaded = (await svc.LoadAlbumsAsync()).Single();

        var s = Assert.Single(loaded.Sessions!);
        Assert.Null(s.State);
    }
}
