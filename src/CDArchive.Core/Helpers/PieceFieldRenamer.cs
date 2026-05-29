using CDArchive.Core.Models;

namespace CDArchive.Core.Helpers;

/// <summary>
/// Walks a piece (and recursively its subpieces and versions, including
/// version subpieces) applying any per-kind pick-list rename dictionaries
/// supplied by the caller. Returns the count of individual field
/// rewrites for status messages.
///
/// <para>Sits in Core (rather than directly inside <c>PickListsViewModel</c>
/// where it began life) so the version-walk is unit-testable and so the
/// helper can be reused if other surfaces ever need the same rewrite
/// (e.g. an import / migration tool).</para>
///
/// <para>Each dictionary is keyed by the OLD value and points at the NEW
/// value. All comparisons are case-insensitive. Pass an empty dictionary
/// (or the one returned by <c>_renames[kind]</c> when no edits happened)
/// to no-op that kind.</para>
/// </summary>
public static class PieceFieldRenamer
{
    /// <summary>
    /// Apply renames to a single top-level <see cref="CanonPiece"/> and
    /// everything it owns. Returns the total count of field rewrites.
    /// </summary>
    public static int ApplyToPiece(
        CanonPiece piece,
        IReadOnlyDictionary<string, string> formRenames,
        IReadOnlyDictionary<string, string> categoryRenames,
        IReadOnlyDictionary<string, string> catalogRenames,
        IReadOnlyDictionary<string, string> keyRenames,
        IReadOnlyDictionary<string, string> instrumentRenames)
    {
        int count = ApplyShared(
            piece, formRenames, categoryRenames, catalogRenames, keyRenames, instrumentRenames,
            getForm:        p => p.Form,
            setForm:        (p, v) => p.Form = v,
            getCategory:    p => p.InstrumentationCategory,
            setCategory:    (p, v) => p.InstrumentationCategory = v,
            getKey:         p => p.KeyTonality,
            setKey:         (p, v) => p.KeyTonality = v,
            getCatalog:     p => p.CatalogInfo,
            getInst:        p => p.Instrumentation,
            setInst:        (p, v) => p.Instrumentation = v);

        if (piece.Subpieces is { Count: > 0 })
            foreach (var sub in piece.Subpieces)
                count += ApplyToPiece(sub, formRenames, categoryRenames,
                                      catalogRenames, keyRenames, instrumentRenames);

        if (piece.Versions is { Count: > 0 })
            foreach (var ver in piece.Versions)
                count += ApplyToVersion(ver, formRenames, categoryRenames,
                                        catalogRenames, keyRenames, instrumentRenames);

        return count;
    }

    private static int ApplyToVersion(
        CanonPieceVersion ver,
        IReadOnlyDictionary<string, string> formRenames,
        IReadOnlyDictionary<string, string> categoryRenames,
        IReadOnlyDictionary<string, string> catalogRenames,
        IReadOnlyDictionary<string, string> keyRenames,
        IReadOnlyDictionary<string, string> instrumentRenames)
    {
        int count = ApplyShared(
            ver, formRenames, categoryRenames, catalogRenames, keyRenames, instrumentRenames,
            getForm:        v => v.Form,
            setForm:        (v, x) => v.Form = x,
            getCategory:    v => v.InstrumentationCategory,
            setCategory:    (v, x) => v.InstrumentationCategory = x,
            getKey:         v => v.KeyTonality,
            setKey:         (v, x) => v.KeyTonality = x,
            getCatalog:     v => v.CatalogInfo,
            getInst:        v => v.Instrumentation,
            setInst:        (v, x) => v.Instrumentation = x);

        if (ver.Subpieces is { Count: > 0 })
            foreach (var sub in ver.Subpieces)
                count += ApplyToPiece(sub, formRenames, categoryRenames,
                                      catalogRenames, keyRenames, instrumentRenames);

        return count;
    }

    /// <summary>
    /// Field-by-field renamer used by both <see cref="ApplyToPiece"/> and
    /// <see cref="ApplyToVersion"/>. CanonPiece and CanonPieceVersion don't
    /// share an interface for these scalar fields, so the helper uses
    /// generic-typed accessor delegates against each owner shape.
    /// </summary>
    private static int ApplyShared<T>(
        T owner,
        IReadOnlyDictionary<string, string> formRenames,
        IReadOnlyDictionary<string, string> categoryRenames,
        IReadOnlyDictionary<string, string> catalogRenames,
        IReadOnlyDictionary<string, string> keyRenames,
        IReadOnlyDictionary<string, string> instrumentRenames,
        Func<T, string?>                          getForm,
        Action<T, string?>                        setForm,
        Func<T, string?>                          getCategory,
        Action<T, string?>                        setCategory,
        Func<T, string?>                          getKey,
        Action<T, string?>                        setKey,
        Func<T, List<CatalogInfo>?>               getCatalog,
        Func<T, System.Text.Json.JsonElement?>    getInst,
        Action<T, System.Text.Json.JsonElement?>  setInst)
    {
        int count = 0;

        if (getForm(owner) is { } form && formRenames.TryGetValue(form, out var nf))
        { setForm(owner, nf); count++; }

        if (getCategory(owner) is { } cat && categoryRenames.TryGetValue(cat, out var nc))
        { setCategory(owner, nc); count++; }

        if (getKey(owner) is { } key && keyRenames.TryGetValue(key, out var nk))
        { setKey(owner, nk); count++; }

        var catalog = getCatalog(owner);
        if (catalog is not null)
        {
            foreach (var ci in catalog)
            {
                if (ci.Catalog is { } cat2 && catalogRenames.TryGetValue(cat2, out var ncat))
                { ci.Catalog = ncat; count++; }
            }
        }

        if (instrumentRenames.Count > 0)
        {
            var rewritten = InstrumentationRenamer.ApplyRenames(
                getInst(owner), instrumentRenames, out var instCount);
            if (instCount > 0)
            {
                setInst(owner, rewritten);
                count += instCount;
            }
        }

        return count;
    }
}
