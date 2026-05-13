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
    public void LegacyFirstLineKey_IsMigratedIntoMarkers()
    {
        const string legacyJson = """
        {
          "title": "Aria",
          "first_line": "Wenn mein Schatz Hochzeit macht"
        }
        """;

        var piece = JsonSerializer.Deserialize<CanonPiece>(legacyJson, ReadOptions);
        Assert.NotNull(piece);
        var marker = Assert.Single(piece!.Markers ?? new());
        Assert.Equal(MarkerKind.FirstLine, marker.Kind);
        Assert.Equal("Wenn mein Schatz Hochzeit macht", marker.Value);
    }

    [Fact]
    public void LegacyKeysAndNewMarkers_Coexist_WithoutDuplication()
    {
        // A JSON that has both the legacy `tempos` shape AND a `markers` array
        // (which would be the case for a snapshot exported during the migration
        // window). The mirror setter dedups on (kind, value, number) so the
        // result is the same set, no duplicates.
        const string mixedJson = """
        {
          "title": "Movement",
          "tempos": [{ "number": 1, "tempo_description": "Allegro" }],
          "markers": [
            { "kind": "Tempo", "value": "Allegro", "number": 1 },
            { "kind": "FirstLine", "value": "Erbarme dich" }
          ]
        }
        """;

        var piece = JsonSerializer.Deserialize<CanonPiece>(mixedJson, ReadOptions);
        Assert.NotNull(piece);
        Assert.NotNull(piece!.Markers);
        Assert.Equal(2, piece.Markers!.Count);
        Assert.Single(piece.Markers, m => m.Kind == MarkerKind.Tempo &&
                                          m.Value == "Allegro" && m.Number == 1);
        Assert.Single(piece.Markers, m => m.Kind == MarkerKind.FirstLine);
    }

    [Fact]
    public void NewMarkersJson_RoundTrips_WithoutLegacyKeys()
    {
        var piece = new CanonPiece
        {
            Title = "Round-trip test",
            Markers =
            [
                new MusicalMarker { Kind = MarkerKind.Tempo,     Value = "Allegro" },
                new MusicalMarker { Kind = MarkerKind.FirstLine, Value = "Hello" },
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
