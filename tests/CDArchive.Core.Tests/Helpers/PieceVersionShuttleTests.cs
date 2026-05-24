using System.Reflection;
using System.Text.Json;
using CDArchive.Core.Helpers;
using CDArchive.Core.Models;

namespace CDArchive.Core.Tests.Helpers;

/// <summary>
/// Locks in the contract that <see cref="PieceVersionShuttle"/> shuttles
/// every property shared by <see cref="CanonPiece"/> and
/// <see cref="CanonPieceVersion"/> in both directions (H14). The
/// reflection test in particular guards against future drift: when a new
/// field is added to both model classes, the round-trip catches it
/// automatically — exactly the failure mode the pre-shuttle code hit
/// with <c>TextAuthor</c>.
/// </summary>
public class PieceVersionShuttleTests
{
    /// <summary>
    /// Reflection contract: every public settable property that exists on
    /// BOTH <see cref="CanonPiece"/> and <see cref="CanonPieceVersion"/>
    /// with the same name and type — minus the explicit piece-only /
    /// version-only exception lists — round-trips through
    /// FromVersion → IntoVersion with its value preserved.
    /// </summary>
    [Fact]
    public void SharedProperties_RoundTrip_PreservesValues()
    {
        var sharedProps = GetSharedShuttledProperties();
        Assert.True(sharedProps.Count > 15,
            $"Expected to discover >15 shared properties; found {sharedProps.Count}. " +
            $"Has the model been gutted or the exception lists overgrown?");

        var version = new CanonPieceVersion();
        foreach (var (pieceProp, versionProp) in sharedProps)
        {
            // Set a sentinel value on the version side that's distinguishable
            // from the type's default.
            var sentinel = MakeSentinel(versionProp.PropertyType, versionProp.Name);
            versionProp.SetValue(version, sentinel);
        }

        // FromVersion → IntoVersion → original version's properties should
        // each carry the value they had before the round-trip.
        var workingPiece = PieceVersionShuttle.FromVersion(version, showSubpieceNumbersDefault: true);
        var targetVersion = new CanonPieceVersion();
        PieceVersionShuttle.IntoVersion(workingPiece, targetVersion);

        foreach (var (pieceProp, versionProp) in sharedProps)
        {
            var expected = versionProp.GetValue(version);
            var actual   = versionProp.GetValue(targetVersion);
            Assert.True(ValuesEqual(expected, actual),
                $"Property {versionProp.Name} dropped during shuttle round-trip. " +
                $"Expected: {Format(expected)}, Actual: {Format(actual)}. " +
                $"Add the property to PieceVersionShuttle.FromVersion + IntoVersion, " +
                $"or add it to PieceOnlyPropertyNames / VersionOnlyPropertyNames if it's intentionally not shuttled.");
        }
    }

    /// <summary>
    /// Regression: <c>TextAuthor</c> exists on both classes but was
    /// missing from the original PieceEditorWindow shuttle methods, so
    /// editing a version silently wiped its librettist/lyricist credit.
    /// </summary>
    [Fact]
    public void TextAuthor_SurvivesRoundTrip()
    {
        var textAuthor = JsonSerializer.SerializeToElement(new[] { "Da Ponte, Lorenzo" });
        var v = new CanonPieceVersion { TextAuthor = textAuthor };

        var piece = PieceVersionShuttle.FromVersion(v, showSubpieceNumbersDefault: true);
        Assert.True(piece.TextAuthor.HasValue);

        var target = new CanonPieceVersion();
        PieceVersionShuttle.IntoVersion(piece, target);
        Assert.True(target.TextAuthor.HasValue);
        Assert.Equal(textAuthor.ToString(), target.TextAuthor!.Value.ToString());
    }

    /// <summary>
    /// The editor's <c>NumberedSubpieces</c> default applies only when the
    /// version has no explicit override — explicit values must survive.
    /// </summary>
    [Fact]
    public void NumberedSubpieces_PreservesExplicitOverride_OverDefault()
    {
        var v = new CanonPieceVersion { NumberedSubpieces = false };

        // Default says "show numbers", but the version explicitly says no.
        var piece = PieceVersionShuttle.FromVersion(v, showSubpieceNumbersDefault: true);
        Assert.False(piece.NumberedSubpieces);
    }

    /// <summary>
    /// Version-only fields (<c>Description</c>, <c>ContributingComposers</c>)
    /// are intentionally NOT touched by the shuttle. Description is edited
    /// separately by the dialog; ContributingComposers isn't exposed at
    /// all today and the editor must preserve its pre-edit value.
    /// </summary>
    [Fact]
    public void VersionOnlyFields_AreNotClobberedByIntoVersion()
    {
        var contributing = JsonSerializer.SerializeToElement(new[] { "orch: Mahler" });
        var v = new CanonPieceVersion
        {
            Description           = "Original version",
            ContributingComposers = contributing,
        };

        var piece = PieceVersionShuttle.FromVersion(v, showSubpieceNumbersDefault: true);
        PieceVersionShuttle.IntoVersion(piece, v);

        Assert.Equal("Original version", v.Description);
        Assert.True(v.ContributingComposers.HasValue);
        Assert.Equal(contributing.ToString(), v.ContributingComposers!.Value.ToString());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static List<(PropertyInfo Piece, PropertyInfo Version)> GetSharedShuttledProperties()
    {
        const BindingFlags publicInstance = BindingFlags.Public | BindingFlags.Instance;
        var pieceProps = typeof(CanonPiece).GetProperties(publicInstance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToDictionary(p => p.Name, p => p);
        var versionProps = typeof(CanonPieceVersion).GetProperties(publicInstance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToDictionary(p => p.Name, p => p);

        return pieceProps.Where(kv =>
                versionProps.ContainsKey(kv.Key)
                && versionProps[kv.Key].PropertyType == kv.Value.PropertyType
                && !PieceVersionShuttle.PieceOnlyPropertyNames.Contains(kv.Key)
                && !PieceVersionShuttle.VersionOnlyPropertyNames.Contains(kv.Key))
            .Select(kv => (Piece: kv.Value, Version: versionProps[kv.Key]))
            .ToList();
    }

    /// <summary>
    /// Build a value for the given type that's distinguishable from the
    /// default for that type. The reflection test only needs the value to
    /// survive the round-trip — it doesn't care about semantic validity.
    /// </summary>
    private static object? MakeSentinel(Type t, string propertyName)
    {
        if (t == typeof(string))           return $"sentinel-{propertyName}";
        if (t == typeof(int?))             return propertyName.Length;
        if (t == typeof(bool?))            return propertyName.Length % 2 == 0;
        if (t == typeof(JsonElement?))     return JsonSerializer.SerializeToElement(new { sentinel = propertyName });
        if (t == typeof(List<CatalogInfo>))         return new List<CatalogInfo> { new() { Catalog = propertyName } };
        if (t == typeof(List<ComposerCredit>))      return new List<ComposerCredit> { new() { Name = propertyName } };
        if (t == typeof(List<VariantInfo>))         return new List<VariantInfo> { new() { Description = propertyName } };
        if (t == typeof(List<MusicalMarker>))       return new List<MusicalMarker> { new() { Value = propertyName, Kind = MarkerKind.Tempo } };
        if (t == typeof(List<CanonPiece>))          return new List<CanonPiece> { new() { Title = propertyName } };
        // Unknown type — make the test loudly fail rather than silently no-op
        // so the next contributor knows to update MakeSentinel here.
        throw new InvalidOperationException(
            $"PieceVersionShuttleTests.MakeSentinel: unhandled property type {t.FullName} for {propertyName}. " +
            $"Add a sentinel-value case so the reflection contract test can exercise it.");
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a is JsonElement ja && b is JsonElement jb)
            return ja.ToString() == jb.ToString();
        // For collections, FromVersion does shallow .ToList() copies so the
        // post-round-trip list is a different instance. Compare structurally
        // via JSON round-trip — the round-trip itself preserves enough
        // structure for the contract test (we don't need polymorphism-safe
        // equality).
        if (a is System.Collections.IEnumerable && b is System.Collections.IEnumerable)
            return JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
        return a.Equals(b);
    }

    private static string Format(object? value) => value switch
    {
        null            => "null",
        JsonElement j   => j.ToString(),
        string s        => $"\"{s}\"",
        _               => value.ToString() ?? "?",
    };
}
