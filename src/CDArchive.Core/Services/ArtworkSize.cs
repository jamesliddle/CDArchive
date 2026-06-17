namespace CDArchive.Core.Services;

/// <summary>
/// The three cover-art thumbnail sizes the user can pick per surface (album
/// list, track list, player). The concrete pixel dimensions are a view concern
/// — the App layer maps each value to display + decode sizes per surface.
/// </summary>
public enum ArtworkSize
{
    Small,
    Medium,
    Large,
}
