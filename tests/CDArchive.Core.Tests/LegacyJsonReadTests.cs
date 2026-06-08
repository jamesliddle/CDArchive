using System.Text.Json;
using CDArchive.Core.Models;

namespace CDArchive.Core.Tests;

/// <summary>
/// Backward-compatibility tests: legacy JSON snapshots (with the pre-migration
/// <c>tempos</c> and <c>first_line</c> keys) must still deserialize cleanly,
/// landing in the unified <see cref="CanonPiece.Markers"/> list as kind=Tempo
/// / kind=FirstLine entries. This protects existing <c>data/*.bak.*</c> backups
/// against being un-restorable after the Tempo→Marker migration.
/// </summary>
public class LegacyJsonReadTests
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void LegacyTemposKey_IsMigratedIntoMarkers()
    {
        const string legacyJson = """
        {
          "title": "Symphony No. 1",
          "tempos": [
            { "number": 1, "tempo_description": "Adagio" },
            { "number": 2, "tempo_description": "Allegro vivace" }
          ]
        }
        """;

        var piece = JsonSerializer.Deserialize<CanonPiece>(legacyJson, ReadOptions);
        Assert.NotNull(piece);
        Assert.NotNull(piece!.Markers);
        Assert.Equal(2, piece.Markers!.Count);
        Assert.All(piece.Markers, m => Assert.Equal(MarkerKind.Tempo, m.Kind));
        Assert.Equal("Adagio",         piece.Markers[0].Value);
        Assert.Equal("Allegro vivace", piece.Markers[1].Value);
    }

    [Fact]
    public void LegacyFirstLineKey_FoldsIntoTitle_WhenTitleEmpty()
    {
        // FirstLine was retired as a marker kind: an old JSON snapshot's
        // `first_line` key now folds into Title (set-if-empty), not into a
        // marker. With no title in the JSON, the first line becomes the title.
        const string legacyJson = """
        {
          "first_line": "Wenn mein Schatz Hochzeit macht"
        }
        """;

        var piece = JsonSerializer.Deserialize<CanonPiece>(legacyJson, ReadOptions);
        Assert.NotNull(piece);
        Assert.Equal("Wenn mein Schatz Hochzeit macht", piece!.Title);
        // No FirstLine marker is synthesised any more.
        Assert.True(piece.Markers is null or { Count: 0 });
    }

    [Fact]
    public void LegacyFirstLineKey_TitlePresent_KeepsTitle_DropsFirstLine()
    {
        // When the snapshot already carries a real title, the set-if-empty
        // shim leaves it alone and the first line is dropped (the migration
        // contract: don't clobber an existing title).
        const string legacyJson = """
        {
          "title": "Aria",
          "first_line": "Wenn mein Schatz Hochzeit macht"
        }
        """;

        var piece = JsonSerializer.Deserialize<CanonPiece>(legacyJson, ReadOptions);
        Assert.NotNull(piece);
        Assert.Equal("Aria", piece!.Title);
        Assert.True(piece.Markers is null or { Count: 0 });
    }

    [Fact]
    public void LegacyTemposKeyAndNewMarkers_Coexist_WithoutDuplication()
    {
        // A JSON that has both the legacy `tempos` shape AND a `markers` array.
        // The Tempo mirror setter dedups on (kind, value, number) so the
        // result is the same set, no duplicates. (FirstLine markers are no
        // longer part of this picture — that kind is retired.)
        const string mixedJson = """
        {
          "title": "Movement",
          "tempos": [{ "number": 1, "tempo_description": "Allegro" }],
          "markers": [
            { "kind": "Tempo", "value": "Allegro", "number": 1 }
          ]
        }
        """;

        var piece = JsonSerializer.Deserialize<CanonPiece>(mixedJson, ReadOptions);
        Assert.NotNull(piece);
        Assert.NotNull(piece!.Markers);
        var marker = Assert.Single(piece.Markers!);
        Assert.Equal(MarkerKind.Tempo, marker.Kind);
        Assert.Equal("Allegro", marker.Value);
        Assert.Equal(1, marker.Number);
    }

    [Fact]
    public void NewMarkersJson_RoundTrips_WithoutLegacyKeys()
    {
        var piece = new CanonPiece
        {
            Title = "Round-trip test",
            Markers =
            [
                new MusicalMarker { Kind = MarkerKind.Tempo, Value = "Allegro" },
            ],
        };

        var json = JsonSerializer.Serialize(piece, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });

        // The serialized output must NOT contain the legacy keys.
        Assert.DoesNotContain("\"tempos\"",     json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"first_line\"", json, StringComparison.Ordinal);
        Assert.Contains("\"markers\"", json, StringComparison.Ordinal);
    }
}
