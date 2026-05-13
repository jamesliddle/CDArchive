namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for <c>piece_composer_credits</c>. Corresponds to
/// <c>ComposerCredit</c> in the domain model. When the credit matches a known
/// composer, <see cref="ComposerId"/> is populated for referential integrity.
/// Exactly one of <see cref="PieceId"/> / <see cref="VersionId"/> is non-null.
/// </summary>
public class PieceComposerCreditRow
{
    public long Id { get; set; }

    public long? PieceId { get; set; }
    public PieceRow? Piece { get; set; }

    public long? VersionId { get; set; }
    public PieceVersionRow? Version { get; set; }

    public int Position { get; set; }

    /// <summary>Nullable — a contributor may not be a catalogued composer.</summary>
    public long? ComposerId { get; set; }
    public ComposerRow? Composer { get; set; }

    /// <summary>Raw credit name (kept even when ComposerId is set, for history / display).</summary>
    public string Name { get; set; } = "";

    /// <summary>Creative role, e.g. "arr.", "orch." — null for the principal composer.</summary>
    public string? Role { get; set; }
}
