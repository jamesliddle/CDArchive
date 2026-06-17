using CDArchive.Core.Services;

namespace CDArchive.App.Helpers;

/// <summary>
/// Maps the user's <see cref="ArtworkSize"/> choice to concrete pixel
/// dimensions per surface. The display box (the rounded thumbnail) sizes are a
/// view concern; the decode width is generous-but-fixed per surface (covers the
/// largest display size at ~2× DPI) so one cached decode serves every size — the
/// size setting only changes how big the thumbnail is drawn, not its sharpness.
/// </summary>
public static class ArtworkSizes
{
    /// <summary>Thumbnail box size (DIP) for the album / track lists.</summary>
    public static double ListBox(ArtworkSize size) => size switch
    {
        ArtworkSize.Small  => 24,
        ArtworkSize.Large  => 46,
        _                  => 34, // Medium
    };

    /// <summary>Thumbnail box size (DIP) for the player bar.</summary>
    public static double PlayerBox(ArtworkSize size) => size switch
    {
        ArtworkSize.Small  => 34,
        ArtworkSize.Large  => 64,
        _                  => 48, // Medium
    };

    /// <summary>Decode width for list thumbnails (covers Large@2×).</summary>
    public const int ListDecodeWidth = 96;

    /// <summary>Decode width for the player thumbnail (covers Large@2×).</summary>
    public const int PlayerDecodeWidth = 144;
}
