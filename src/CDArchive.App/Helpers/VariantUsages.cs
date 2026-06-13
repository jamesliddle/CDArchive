using System.Windows;
using CDArchive.App.ViewModels;
using CDArchive.App.Views;
using CDArchive.Core.Models;

namespace CDArchive.App.Helpers;

/// <summary>
/// Shared presenter for the "recordings using this variant" flow, so every
/// piece-editor entry point (Canon tree edits, TrackEditor's "Edit Root Piece")
/// behaves identically. Shows the <see cref="PieceAlbumsWindow"/> list and, when
/// the user picks "Open Album", opens the <see cref="AlbumEditorWindow"/> on top
/// of the supplied owner so they can clear the variant without losing the
/// in-progress piece edit.
/// </summary>
internal static class VariantUsages
{
    /// <summary>
    /// Lists the album / loose-track recordings identifying <paramref name="variant"/>
    /// (owned by <paramref name="owner"/>). On "Open Album" opens the album editor
    /// on top and, when the owner is a <see cref="PieceEditorWindow"/>, refreshes
    /// its click-time usage snapshot so a now-cleared variant becomes removable.
    /// </summary>
    public static async Task ShowAsync(
        Window owner, VariantInfo variant, CanonViewModel canonVm, AlbumsViewModel albumsVm)
    {
        try
        {
            var hits = await canonVm.GetVariantUsageHitsAsync(variant.Id);
            if (hits.Count == 0)
            {
                MessageBox.Show(owner,
                    $"No recordings currently identify the variant \"{variant.Description}\".",
                    "Recordings", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new PieceAlbumsWindow(
                $"Recordings using variant: {variant.Description}", hits, albumsVm.Player)
            {
                Owner = owner
            };
            dlg.ShowDialog();

            if (dlg.SelectedAlbum is CanonAlbum album)
            {
                // Re-entrancy guard: refuse to open a second AlbumEditor on top of
                // one already in the stack (e.g. AlbumEditor → Edit Track → Edit
                // Root Piece → Recordings → Open Album). Editing the same album at
                // two stack levels would orphan the outer editor's clone — its
                // stale copy would re-Add as a duplicate and overwrite this edit on
                // the outer OK. No safe merge exists, so block + explain.
                if (AlbumEditorWindow.IsAnyOpen)
                {
                    MessageBox.Show(owner,
                        $"\"{album.DisplayTitle}\" can't be opened from here because an album " +
                        "editor is already open.\n\nFinish (or cancel) that album edit first, then " +
                        "open this album from the Albums or Tracks view to clear the variant.",
                        "Album already open", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                await OpenAlbumEditorAsync(owner, album, albumsVm);
                if (owner is PieceEditorWindow pe)
                    pe.VariantUsageCounts = await canonVm.GetReferencedVariantCountsAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "Recordings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Opens the album editor for <paramref name="album"/> owned by
    /// <paramref name="owner"/>, persisting the edit through the shared
    /// <see cref="AlbumsViewModel"/> so the PieceReferenceIndex is rebuilt. Also
    /// the backing implementation for <c>CanonView.OpenAlbumEditorAsync</c>.
    /// </summary>
    public static async Task OpenAlbumEditorAsync(
        Window owner, CanonAlbum album, AlbumsViewModel albumsVm)
    {
        // Ensure we're editing the live in-memory instance (not a stale copy).
        if (albumsVm.AllAlbums.Count == 0) await albumsVm.LoadDataCommand.ExecuteAsync(null);

        // Resolve the live AllAlbums instance. Reference-equality alone is NOT
        // enough: `album` may come from a PieceReferenceIndex hit built from a
        // different album-instance set than AllAlbums (startup order). A miss
        // would add the edited clone as a duplicate, leaving the original's
        // stale piece-refs in place ("new count up, old count stays"). The
        // IdentityKey fallback in ResolveLiveAlbum maps it to the live instance.
        var liveAlbum = albumsVm.ResolveLiveAlbum(album);

        var (pieces, pickLists) = await albumsVm.LoadEditorDataAsync();
        var dlg = new AlbumEditorWindow(pickLists, pieces, albumsVm.Player, liveAlbum)
        {
            Owner = owner
        };
        if (dlg.ShowDialog() != true || dlg.Result is not CanonAlbum result) return;

        var idx = albumsVm.AllAlbums.IndexOf(liveAlbum);
        if (idx >= 0) albumsVm.AllAlbums[idx] = result;
        else          albumsVm.AllAlbums.Add(result);
        albumsVm.ApplyFilter();
        await albumsVm.SaveAsync(pickLists);
    }
}
