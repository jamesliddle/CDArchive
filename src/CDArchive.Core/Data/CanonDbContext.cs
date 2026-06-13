using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Data;

/// <summary>
/// EF Core context for the Canon SQLite database.
/// Schema uses <c>snake_case</c> table and column names to match the JSON property
/// naming convention and to keep DDL readable.
/// </summary>
public class CanonDbContext : DbContext
{
    public CanonDbContext(DbContextOptions<CanonDbContext> options) : base(options) { }

    // ── Composers ────────────────────────────────────────────────────────────
    public DbSet<ComposerRow>                Composers               => Set<ComposerRow>();
    public DbSet<ComposerAliasRow>           ComposerAliases         => Set<ComposerAliasRow>();
    public DbSet<ComposerCatalogPrefixRow>   ComposerCatalogPrefixes => Set<ComposerCatalogPrefixRow>();

    // ── People & ensembles ───────────────────────────────────────────────────
    public DbSet<PersonRow>                  People                  => Set<PersonRow>();
    public DbSet<EnsembleRow>                Ensembles               => Set<EnsembleRow>();
    public DbSet<EnsembleNameRow>            EnsembleNames           => Set<EnsembleNameRow>();
    public DbSet<EnsembleMembershipRow>      EnsembleMemberships     => Set<EnsembleMembershipRow>();

    // ── Pieces ───────────────────────────────────────────────────────────────
    public DbSet<PieceRow>                   Pieces                  => Set<PieceRow>();
    public DbSet<PieceVersionRow>            PieceVersions           => Set<PieceVersionRow>();
    public DbSet<PieceCatalogEntryRow>       PieceCatalogEntries     => Set<PieceCatalogEntryRow>();
    public DbSet<PieceComposerCreditRow>     PieceComposerCredits    => Set<PieceComposerCreditRow>();
    public DbSet<PieceVariantRow>            PieceVariants           => Set<PieceVariantRow>();

    // ── Albums ───────────────────────────────────────────────────────────────
    public DbSet<AlbumRow>                   Albums                  => Set<AlbumRow>();
    public DbSet<AlbumVolumeRow>             AlbumVolumes            => Set<AlbumVolumeRow>();
    public DbSet<AlbumDiscRow>               AlbumDiscs              => Set<AlbumDiscRow>();
    public DbSet<AlbumTrackRow>              AlbumTracks             => Set<AlbumTrackRow>();
    public DbSet<AlbumTrackPieceRefRow>      AlbumTrackPieceRefs     => Set<AlbumTrackPieceRefRow>();
    public DbSet<AlbumTrackPieceRefVariantRow> AlbumTrackPieceRefVariants => Set<AlbumTrackPieceRefVariantRow>();
    public DbSet<AlbumPerformerRow>          AlbumPerformers         => Set<AlbumPerformerRow>();

    // ── Markers (track anchors: tempo / first-line / rehearsal mark / bar number) ───
    public DbSet<PieceMarkerRow>             PieceMarkers            => Set<PieceMarkerRow>();

    // ── Pick lists ───────────────────────────────────────────────────────────
    public DbSet<PickListValueRow>           PickListValues          => Set<PickListValueRow>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);

        ConfigureComposers(mb);
        ConfigurePeopleAndEnsembles(mb);
        ConfigurePieces(mb);
        ConfigureAlbums(mb);
        ConfigurePickLists(mb);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Composers
    // ─────────────────────────────────────────────────────────────────────────
    private static void ConfigureComposers(ModelBuilder mb)
    {
        mb.Entity<ComposerRow>(b =>
        {
            b.ToTable("composers");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");

            b.Property(x => x.Name).HasColumnName("name").IsRequired().UseCollation("NOCASE");
            b.Property(x => x.SortName).HasColumnName("sort_name").IsRequired();
            b.Property(x => x.BirthDate).HasColumnName("birth_date");
            b.Property(x => x.BirthPlace).HasColumnName("birth_local_place");
            b.Property(x => x.BirthState).HasColumnName("birth_state");
            b.Property(x => x.BirthCountry).HasColumnName("birth_country");
            b.Property(x => x.BirthNotes).HasColumnName("birth_notes");
            b.Property(x => x.DeathDate).HasColumnName("death_date");
            b.Property(x => x.DeathPlace).HasColumnName("death_local_place");
            b.Property(x => x.DeathState).HasColumnName("death_state");
            b.Property(x => x.DeathCountry).HasColumnName("death_country");
            b.Property(x => x.Notes).HasColumnName("notes");
            // No HasDefaultValue here: with it, EF Core omits the column from the
            // INSERT statement when the CLR value happens to match the configured
            // default, letting the database apply its own (possibly stale) default
            // instead — which silently inverts IsProvisional for pieces, where an
            // earlier migration set DEFAULT 0. Always include the value in the
            // INSERT so the C# property is the single source of truth.
            b.Property(x => x.IsProvisional).HasColumnName("is_provisional");
            b.Property(x => x.MusicBrainzArtistId).HasColumnName("musicbrainz_artist_id");

            b.HasIndex(x => x.Name).IsUnique();
            b.HasIndex(x => x.SortName);
        });

        mb.Entity<ComposerAliasRow>(b =>
        {
            b.ToTable("composer_aliases");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.ComposerId).HasColumnName("composer_id");
            b.Property(x => x.Position).HasColumnName("position");
            b.Property(x => x.Alias).HasColumnName("alias").IsRequired();

            b.HasOne(x => x.Composer)
                .WithMany(c => c.Aliases)
                .HasForeignKey(x => x.ComposerId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => new { x.ComposerId, x.Position }).IsUnique();
        });

        mb.Entity<ComposerCatalogPrefixRow>(b =>
        {
            b.ToTable("composer_catalog_prefixes");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.ComposerId).HasColumnName("composer_id");
            b.Property(x => x.Position).HasColumnName("position");
            b.Property(x => x.Prefix).HasColumnName("prefix").IsRequired();

            b.HasOne(x => x.Composer)
                .WithMany(c => c.CatalogPrefixes)
                .HasForeignKey(x => x.ComposerId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => new { x.ComposerId, x.Position }).IsUnique();
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // People & ensembles
    // ─────────────────────────────────────────────────────────────────────────
    private static void ConfigurePeopleAndEnsembles(ModelBuilder mb)
    {
        mb.Entity<PersonRow>(b =>
        {
            b.ToTable("people");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.Name).HasColumnName("name").IsRequired().UseCollation("NOCASE");
            b.Property(x => x.SortName).HasColumnName("sort_name").IsRequired();
            b.Property(x => x.BirthDate).HasColumnName("birth_date");
            b.Property(x => x.DeathDate).HasColumnName("death_date");
            b.Property(x => x.Notes).HasColumnName("notes");

            b.HasIndex(x => x.Name).IsUnique();
            b.HasIndex(x => x.SortName);
        });

        mb.Entity<EnsembleRow>(b =>
        {
            b.ToTable("ensembles");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.Kind).HasColumnName("kind");
            b.Property(x => x.SortName).HasColumnName("sort_name").IsRequired();
            b.Property(x => x.FoundedYear).HasColumnName("founded_year");
            b.Property(x => x.DisbandedYear).HasColumnName("disbanded_year");
            b.Property(x => x.Notes).HasColumnName("notes");

            b.HasIndex(x => x.SortName);
        });

        mb.Entity<EnsembleNameRow>(b =>
        {
            b.ToTable("ensemble_names");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.EnsembleId).HasColumnName("ensemble_id");
            b.Property(x => x.Position).HasColumnName("position");
            b.Property(x => x.Name).HasColumnName("name").IsRequired();
            b.Property(x => x.StartDate).HasColumnName("start_date");
            b.Property(x => x.EndDate).HasColumnName("end_date");
            b.Property(x => x.Notes).HasColumnName("notes");

            b.HasOne(x => x.Ensemble)
                .WithMany(e => e.Names)
                .HasForeignKey(x => x.EnsembleId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => new { x.EnsembleId, x.Position }).IsUnique();
            b.HasIndex(x => x.Name);
        });

        mb.Entity<EnsembleMembershipRow>(b =>
        {
            b.ToTable("ensemble_memberships");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.EnsembleId).HasColumnName("ensemble_id");
            b.Property(x => x.PersonId).HasColumnName("person_id");
            b.Property(x => x.Position).HasColumnName("position");
            b.Property(x => x.Role).HasColumnName("role");
            b.Property(x => x.StartDate).HasColumnName("start_date");
            b.Property(x => x.EndDate).HasColumnName("end_date");
            b.Property(x => x.Notes).HasColumnName("notes");

            b.HasOne(x => x.Ensemble)
                .WithMany(e => e.Memberships)
                .HasForeignKey(x => x.EnsembleId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(x => x.Person)
                .WithMany(p => p.Memberships)
                .HasForeignKey(x => x.PersonId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => new { x.EnsembleId, x.Position });
            b.HasIndex(x => x.PersonId);
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pieces
    // ─────────────────────────────────────────────────────────────────────────
    private static void ConfigurePieces(ModelBuilder mb)
    {
        mb.Entity<PieceRow>(b =>
        {
            // Row-level constraints:
            // - ck_pieces_single_parent: a piece can be parented by either
            //   another piece (parent_piece_id) or a version (parent_version_id),
            //   never both.
            // - ck_pieces_has_identity: cheap insurance against a buggy importer
            //   or seed file producing an identity-less row. Every piece must
            //   carry at least one human-visible identifying field (Title, Form,
            //   Nickname, Number, or a Catalogue entry — proxied by the
            //   non-empty catalog_sort_prefix that the save path populates from
            //   CatalogInfo[0]). The composer FK is already enforced via
            //   composer_id NOT NULL. See Rework M6.
            b.ToTable("pieces", t =>
            {
                t.HasCheckConstraint(
                    "ck_pieces_single_parent",
                    "(parent_piece_id IS NULL) OR (parent_version_id IS NULL)");
                t.HasCheckConstraint(
                    "ck_pieces_has_identity",
                    "title IS NOT NULL OR form IS NOT NULL OR nickname IS NOT NULL OR " +
                    "number IS NOT NULL OR catalog_sort_prefix != ''");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.ComposerId).HasColumnName("composer_id");
            b.Property(x => x.ParentPieceId).HasColumnName("parent_piece_id");
            b.Property(x => x.ParentVersionId).HasColumnName("parent_version_id");
            b.Property(x => x.Position).HasColumnName("position");

            b.Property(x => x.Title).HasColumnName("title");
            b.Property(x => x.TitleEnglish).HasColumnName("title_english");
            b.Property(x => x.Subtitle).HasColumnName("subtitle");
            b.Property(x => x.Nickname).HasColumnName("nickname");
            b.Property(x => x.Form).HasColumnName("form");
            b.Property(x => x.Number).HasColumnName("number");
            b.Property(x => x.MusicNumber).HasColumnName("music_number");
            b.Property(x => x.KeyTonality).HasColumnName("key_tonality");
            b.Property(x => x.KeyMode).HasColumnName("key_mode");
            b.Property(x => x.PublicationYear).HasColumnName("publication_year");
            b.Property(x => x.InstrumentationCategory).HasColumnName("instrumentation_category");
            b.Property(x => x.NumberedSubpieces).HasColumnName("numbered_subpieces");
            b.Property(x => x.SubpiecesStart).HasColumnName("subpieces_start");
            b.Property(x => x.Notes).HasColumnName("notes");
            // No HasDefaultValue here: with it, EF Core omits the column from the
            // INSERT statement when the CLR value happens to match the configured
            // default, letting the database apply its own (possibly stale) default
            // instead — which silently inverts IsProvisional for pieces, where an
            // earlier migration set DEFAULT 0. Always include the value in the
            // INSERT so the C# property is the single source of truth.
            b.Property(x => x.IsProvisional).HasColumnName("is_provisional");
            b.Property(x => x.MusicBrainzWorkId).HasColumnName("musicbrainz_work_id");

            b.Property(x => x.InstrumentationJson).HasColumnName("instrumentation_json");
            b.Property(x => x.CompositionYearsJson).HasColumnName("composition_years_json");
            b.Property(x => x.TextAuthorJson).HasColumnName("text_author_json");
            b.Property(x => x.RolesJson).HasColumnName("roles_json");
            b.Property(x => x.ArrangementsJson).HasColumnName("arrangements_json");
            b.Property(x => x.CadenzaJson).HasColumnName("cadenza_json");
            b.Property(x => x.TitleNumberJson).HasColumnName("title_number_json");

            b.Property(x => x.CatalogSortPrefix).HasColumnName("catalog_sort_prefix").IsRequired();
            b.Property(x => x.CatalogSortNumber).HasColumnName("catalog_sort_number");
            b.Property(x => x.CatalogSortSuffix).HasColumnName("catalog_sort_suffix").IsRequired();

            // Top-level piece belongs to a composer; composer can't be deleted while pieces exist.
            b.HasOne(x => x.Composer)
                .WithMany(c => c.Pieces)
                .HasForeignKey(x => x.ComposerId)
                .OnDelete(DeleteBehavior.Restrict);

            // Subpiece of a parent piece — cascade delete.
            b.HasOne(x => x.ParentPiece)
                .WithMany(p => p.Subpieces)
                .HasForeignKey(x => x.ParentPieceId)
                .OnDelete(DeleteBehavior.Cascade);

            // Subpiece of a parent version — cascade delete.
            b.HasOne(x => x.ParentVersion)
                .WithMany(v => v.Subpieces)
                .HasForeignKey(x => x.ParentVersionId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => x.ComposerId);
            b.HasIndex(x => new { x.ParentPieceId, x.Position });
            b.HasIndex(x => new { x.ParentVersionId, x.Position });
            b.HasIndex(x => new { x.ComposerId, x.Title });
            b.HasIndex(x => new { x.ComposerId, x.CatalogSortPrefix, x.CatalogSortNumber, x.CatalogSortSuffix })
                .HasDatabaseName("ix_pieces_composer_catalog_sort");
        });

        mb.Entity<PieceVersionRow>(b =>
        {
            b.ToTable("piece_versions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.PieceId).HasColumnName("piece_id");
            b.Property(x => x.Position).HasColumnName("position");
            b.Property(x => x.Description).HasColumnName("description");

            b.Property(x => x.Title).HasColumnName("title");
            b.Property(x => x.TitleEnglish).HasColumnName("title_english");
            b.Property(x => x.Subtitle).HasColumnName("subtitle");
            b.Property(x => x.Nickname).HasColumnName("nickname");
            b.Property(x => x.Form).HasColumnName("form");
            b.Property(x => x.Number).HasColumnName("number");
            b.Property(x => x.MusicNumber).HasColumnName("music_number");
            b.Property(x => x.KeyTonality).HasColumnName("key_tonality");
            b.Property(x => x.KeyMode).HasColumnName("key_mode");
            b.Property(x => x.PublicationYear).HasColumnName("publication_year");
            b.Property(x => x.InstrumentationCategory).HasColumnName("instrumentation_category");
            b.Property(x => x.NumberedSubpieces).HasColumnName("numbered_subpieces");
            b.Property(x => x.SubpiecesStart).HasColumnName("subpieces_start");
            b.Property(x => x.Notes).HasColumnName("notes");

            b.Property(x => x.InstrumentationJson).HasColumnName("instrumentation_json");
            b.Property(x => x.CompositionYearsJson).HasColumnName("composition_years_json");
            b.Property(x => x.TextAuthorJson).HasColumnName("text_author_json");
            b.Property(x => x.RolesJson).HasColumnName("roles_json");
            b.Property(x => x.ArrangementsJson).HasColumnName("arrangements_json");
            b.Property(x => x.CadenzaJson).HasColumnName("cadenza_json");
            b.Property(x => x.TitleNumberJson).HasColumnName("title_number_json");
            b.Property(x => x.ContributingComposersJson).HasColumnName("contributing_composers_json");

            b.HasOne(x => x.Piece)
                .WithMany(p => p.Versions)
                .HasForeignKey(x => x.PieceId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => new { x.PieceId, x.Position });
        });

        mb.Entity<PieceCatalogEntryRow>(b =>
        {
            b.ToTable("piece_catalog_entries", t => t.HasCheckConstraint(
                "ck_piece_catalog_entries_exactly_one_owner",
                "(piece_id IS NOT NULL AND version_id IS NULL) OR " +
                "(piece_id IS NULL AND version_id IS NOT NULL)"));

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.PieceId).HasColumnName("piece_id");
            b.Property(x => x.VersionId).HasColumnName("version_id");
            b.Property(x => x.Position).HasColumnName("position");
            b.Property(x => x.Catalog).HasColumnName("catalog").IsRequired();
            b.Property(x => x.CatalogNumber).HasColumnName("catalog_number");
            b.Property(x => x.CatalogSubnumber).HasColumnName("catalog_subnumber");

            b.HasOne(x => x.Piece)
                .WithMany(p => p.CatalogEntries)
                .HasForeignKey(x => x.PieceId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(x => x.Version)
                .WithMany(v => v.CatalogEntries)
                .HasForeignKey(x => x.VersionId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => new { x.PieceId, x.Position });
            b.HasIndex(x => new { x.VersionId, x.Position });
        });

        // (piece_tempos / pieces.first_line are gone — Stage 3 of the
        //  Tempo→Marker migration. All anchor data lives in piece_markers,
        //  discriminated by `kind`.)

        mb.Entity<PieceMarkerRow>(b =>
        {
            // Mirrors piece_tempos' multi-owner shape: a marker belongs to
            // exactly one of (piece, version, parent marker). Add discriminator
            // (kind) and value/bar_number columns for the four marker varieties.
            b.ToTable("piece_markers", t => t.HasCheckConstraint(
                "ck_piece_markers_exactly_one_owner",
                "((piece_id IS NOT NULL) + (version_id IS NOT NULL) + (parent_marker_id IS NOT NULL)) = 1"));

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.PieceId).HasColumnName("piece_id");
            b.Property(x => x.VersionId).HasColumnName("version_id");
            b.Property(x => x.ParentMarkerId).HasColumnName("parent_marker_id");
            b.Property(x => x.Position).HasColumnName("position");
            // Persist MarkerKind as text for human-readable DB inspection.
            b.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>().IsRequired();
            b.Property(x => x.Value).HasColumnName("value");
            b.Property(x => x.BarNumber).HasColumnName("bar_number");
            b.Property(x => x.Number).HasColumnName("number");
            b.Property(x => x.Description).HasColumnName("description");

            b.HasOne(x => x.Piece)
                .WithMany(p => p.Markers)
                .HasForeignKey(x => x.PieceId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(x => x.Version)
                .WithMany(v => v.Markers)
                .HasForeignKey(x => x.VersionId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(x => x.ParentMarker)
                .WithMany(m => m.SubMarkers)
                .HasForeignKey(x => x.ParentMarkerId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => new { x.PieceId, x.Position });
            b.HasIndex(x => new { x.VersionId, x.Position });
            b.HasIndex(x => new { x.ParentMarkerId, x.Position });
        });

        mb.Entity<PieceComposerCreditRow>(b =>
        {
            b.ToTable("piece_composer_credits", t => t.HasCheckConstraint(
                "ck_piece_composer_credits_exactly_one_owner",
                "(piece_id IS NOT NULL AND version_id IS NULL) OR " +
                "(piece_id IS NULL AND version_id IS NOT NULL)"));

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.PieceId).HasColumnName("piece_id");
            b.Property(x => x.VersionId).HasColumnName("version_id");
            b.Property(x => x.Position).HasColumnName("position");
            b.Property(x => x.ComposerId).HasColumnName("composer_id");
            b.Property(x => x.Name).HasColumnName("name").IsRequired();
            b.Property(x => x.Role).HasColumnName("role");

            b.HasOne(x => x.Piece)
                .WithMany(p => p.ComposerCredits)
                .HasForeignKey(x => x.PieceId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(x => x.Version)
                .WithMany(v => v.ComposerCredits)
                .HasForeignKey(x => x.VersionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting a composer while credits reference them requires clearing the credits first.
            b.HasOne(x => x.Composer)
                .WithMany()
                .HasForeignKey(x => x.ComposerId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => new { x.PieceId, x.Position });
            b.HasIndex(x => new { x.VersionId, x.Position });
            b.HasIndex(x => x.ComposerId);
        });

        mb.Entity<PieceVariantRow>(b =>
        {
            b.ToTable("piece_variants", t => t.HasCheckConstraint(
                "ck_piece_variants_exactly_one_owner",
                "(piece_id IS NOT NULL AND version_id IS NULL) OR " +
                "(piece_id IS NULL AND version_id IS NOT NULL)"));

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.PieceId).HasColumnName("piece_id");
            b.Property(x => x.VersionId).HasColumnName("version_id");
            b.Property(x => x.Position).HasColumnName("position");
            b.Property(x => x.Description).HasColumnName("description").IsRequired();
            b.Property(x => x.LongDescription).HasColumnName("long_description");

            b.HasOne(x => x.Piece)
                .WithMany(p => p.Variants)
                .HasForeignKey(x => x.PieceId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(x => x.Version)
                .WithMany(v => v.Variants)
                .HasForeignKey(x => x.VersionId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => new { x.PieceId, x.Position });
            b.HasIndex(x => new { x.VersionId, x.Position });
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Albums
    // ─────────────────────────────────────────────────────────────────────────
    private static void ConfigureAlbums(ModelBuilder mb)
    {
        mb.Entity<AlbumRow>(b =>
        {
            b.ToTable("albums");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.Title).HasColumnName("title");
            b.Property(x => x.Subtitle).HasColumnName("subtitle");
            b.Property(x => x.Label).HasColumnName("label");
            b.Property(x => x.CatalogueNumber).HasColumnName("catalogue_number");
            b.Property(x => x.Barcode).HasColumnName("barcode");
            b.Property(x => x.SparsCode).HasColumnName("spars_code");
            b.Property(x => x.IsStereo).HasColumnName("is_stereo");
            b.Property(x => x.Notes).HasColumnName("notes");
            b.Property(x => x.ArchiveFolder).HasColumnName("archive_folder");
            // Recording-session fields (moved off the retired album_sessions
            // table). Engineers + Producers serialize as JSON arrays of strings
            // — same shape the per-track copies on AlbumTrackRow use.
            b.Property(x => x.SessionDates).HasColumnName("session_dates");
            b.Property(x => x.SessionVenue).HasColumnName("session_venue");
            b.Property(x => x.SessionCity).HasColumnName("session_city");
            b.Property(x => x.SessionState).HasColumnName("session_state");
            b.Property(x => x.SessionCountry).HasColumnName("session_country");
            b.Property(x => x.SessionEngineersJson).HasColumnName("session_engineers_json");
            b.Property(x => x.SessionProducersJson).HasColumnName("session_producers_json");
            // No HasDefaultValue here: with it, EF Core omits the column from the
            // INSERT statement when the CLR value happens to match the configured
            // default, letting the database apply its own (possibly stale) default
            // instead — which silently inverts IsProvisional for pieces, where an
            // earlier migration set DEFAULT 0. Always include the value in the
            // INSERT so the C# property is the single source of truth.
            b.Property(x => x.IsProvisional).HasColumnName("is_provisional");
            b.Property(x => x.MusicBrainzReleaseId).HasColumnName("musicbrainz_release_id");

            // Filtered unique index: uniqueness only enforced when both columns are non-null.
            b.HasIndex(x => new { x.Label, x.CatalogueNumber })
                .IsUnique()
                .HasFilter("\"label\" IS NOT NULL AND \"catalogue_number\" IS NOT NULL")
                .HasDatabaseName("ux_albums_label_catalogue_number");

            b.HasIndex(x => x.Title);
        });

        mb.Entity<AlbumVolumeRow>(b =>
        {
            b.ToTable("album_volumes");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.AlbumId).HasColumnName("album_id");
            b.Property(x => x.Number).HasColumnName("number");
            b.Property(x => x.Title).HasColumnName("title");
            b.Property(x => x.Subtitle).HasColumnName("subtitle");

            b.HasOne(x => x.Album)
                .WithMany(a => a.Volumes)
                .HasForeignKey(x => x.AlbumId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => new { x.AlbumId, x.Number }).IsUnique();
        });

        mb.Entity<AlbumDiscRow>(b =>
        {
            b.ToTable("album_discs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.AlbumId).HasColumnName("album_id");
            b.Property(x => x.VolumeId).HasColumnName("volume_id");
            b.Property(x => x.DiscNumber).HasColumnName("disc_number");
            b.Property(x => x.Title).HasColumnName("title");
            b.Property(x => x.FolderName).HasColumnName("folder_name");

            b.HasOne(x => x.Album)
                .WithMany(a => a.Discs)
                .HasForeignKey(x => x.AlbumId)
                .OnDelete(DeleteBehavior.Cascade);

            // Volume delete requires caller to move or remove discs first.
            b.HasOne(x => x.Volume)
                .WithMany(v => v.Discs)
                .HasForeignKey(x => x.VolumeId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => new { x.AlbumId, x.VolumeId, x.DiscNumber });
        });

        mb.Entity<AlbumTrackRow>(b =>
        {
            b.ToTable("album_tracks");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.DiscId).HasColumnName("disc_id");
            b.Property(x => x.TrackNumber).HasColumnName("track_number");
            b.Property(x => x.Duration).HasColumnName("duration");
            b.Property(x => x.Description).HasColumnName("description");
            // Per-track recording-session fields. Same shape as the album-level
            // columns; the TrackEditor eagerly copies album defaults into blank
            // track fields on open.
            b.Property(x => x.SessionDates).HasColumnName("session_dates");
            b.Property(x => x.SessionVenue).HasColumnName("session_venue");
            b.Property(x => x.SessionCity).HasColumnName("session_city");
            b.Property(x => x.SessionState).HasColumnName("session_state");
            b.Property(x => x.SessionCountry).HasColumnName("session_country");
            b.Property(x => x.SessionEngineersJson).HasColumnName("session_engineers_json");
            b.Property(x => x.SessionProducersJson).HasColumnName("session_producers_json");
            b.Property(x => x.SparsCode).HasColumnName("spars_code");
            b.Property(x => x.IsStereo).HasColumnName("is_stereo");
            // No HasDefaultValue here: with it, EF Core omits the column from the
            // INSERT statement when the CLR value happens to match the configured
            // default, letting the database apply its own (possibly stale) default
            // instead — which silently inverts IsProvisional for pieces, where an
            // earlier migration set DEFAULT 0. Always include the value in the
            // INSERT so the C# property is the single source of truth.
            b.Property(x => x.IsProvisional).HasColumnName("is_provisional");
            b.Property(x => x.FlacPath).HasColumnName("flac_path");
            b.Property(x => x.Mp3Path).HasColumnName("mp3_path");

            b.HasOne(x => x.Disc)
                .WithMany(d => d.Tracks)
                .HasForeignKey(x => x.DiscId)
                .OnDelete(DeleteBehavior.Cascade);

            // No Session FK any more — session fields live directly on each
            // track row. The album_sessions table + AlbumSessionRow.Session
            // navigation were retired in the session-as-fields refactor.

            b.HasIndex(x => new { x.DiscId, x.TrackNumber }).IsUnique();
        });

        mb.Entity<AlbumTrackPieceRefRow>(b =>
        {
            b.ToTable("album_track_piece_refs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TrackId).HasColumnName("track_id");
            b.Property(x => x.Position).HasColumnName("position");
            b.Property(x => x.PieceId).HasColumnName("piece_id");
            b.Property(x => x.VersionId).HasColumnName("version_id");
            b.Property(x => x.EndPieceId).HasColumnName("end_piece_id");
            b.Property(x => x.StartMarkerId).HasColumnName("start_marker_id");
            b.Property(x => x.EndMarkerId).HasColumnName("end_marker_id");
            b.Property(x => x.DisplayLabel).HasColumnName("display_label");

            b.HasOne(x => x.Track)
                .WithMany(t => t.PieceRefs)
                .HasForeignKey(x => x.TrackId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting a piece with album refs requires clearing the refs first — prevents silent data loss.
            b.HasOne(x => x.Piece)
                .WithMany(p => p.AlbumRefs)
                .HasForeignKey(x => x.PieceId)
                .OnDelete(DeleteBehavior.Restrict);

            // Version is optional; deleting a version reverts refs to main piece.
            b.HasOne(x => x.Version)
                .WithMany(v => v.AlbumRefs)
                .HasForeignKey(x => x.VersionId)
                .OnDelete(DeleteBehavior.SetNull);

            // End-piece (range refs) — same restrict semantics as the start piece.
            // No inverse navigation: pieces don't need to enumerate "refs that
            // end at me" the way they enumerate "refs that start at me".
            b.HasOne(x => x.EndPiece)
                .WithMany()
                .HasForeignKey(x => x.EndPieceId)
                .OnDelete(DeleteBehavior.Restrict);

            // Start / end markers — optional, with SetNull on cascade so deleting
            // a marker drops the anchor without nuking the whole ref. The ref
            // remains valid as a coarse subpiece-level reference.
            b.HasOne(x => x.StartMarker)
                .WithMany()
                .HasForeignKey(x => x.StartMarkerId)
                .OnDelete(DeleteBehavior.SetNull);

            b.HasOne(x => x.EndMarker)
                .WithMany()
                .HasForeignKey(x => x.EndMarkerId)
                .OnDelete(DeleteBehavior.SetNull);

            b.HasIndex(x => new { x.TrackId, x.Position });
            b.HasIndex(x => x.PieceId);
        });

        mb.Entity<AlbumTrackPieceRefVariantRow>(b =>
        {
            b.ToTable("album_track_piece_ref_variants");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.RefId).HasColumnName("ref_id");
            b.Property(x => x.VariantId).HasColumnName("variant_id");
            b.Property(x => x.Position).HasColumnName("position");

            // The join row dies with its ref.
            b.HasOne(x => x.Ref)
                .WithMany(r => r.Variants)
                .HasForeignKey(x => x.RefId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict: a variant a recording still identifies can't be deleted
            // out from under it. The piece-save path pre-flights this and
            // surfaces a friendly message rather than a raw FK failure.
            b.HasOne(x => x.Variant)
                .WithMany()
                .HasForeignKey(x => x.VariantId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => new { x.RefId, x.Position });
            b.HasIndex(x => x.VariantId);
        });

        mb.Entity<AlbumPerformerRow>(b =>
        {
            b.ToTable("album_performers", t =>
            {
                t.HasCheckConstraint(
                    "ck_album_performers_person_xor_ensemble",
                    "(person_id IS NULL) OR (ensemble_id IS NULL)");
                t.HasCheckConstraint(
                    "ck_album_performers_has_identity",
                    "(person_id IS NOT NULL) OR (ensemble_id IS NOT NULL) OR (display_name IS NOT NULL)");
                // Loose-track performers anchor on track_id only (album_id null);
                // album-level credits anchor on album_id only (track_id null);
                // album-bound track overrides anchor on both. Any row with both
                // null would be unowned and is rejected.
                t.HasCheckConstraint(
                    "ck_album_performers_has_owner",
                    "(album_id IS NOT NULL) OR (track_id IS NOT NULL)");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.AlbumId).HasColumnName("album_id");
            b.Property(x => x.TrackId).HasColumnName("track_id");
            b.Property(x => x.Position).HasColumnName("position");
            b.Property(x => x.PersonId).HasColumnName("person_id");
            b.Property(x => x.EnsembleId).HasColumnName("ensemble_id");
            b.Property(x => x.DisplayName).HasColumnName("display_name");
            b.Property(x => x.Role).HasColumnName("role");
            b.Property(x => x.Instrument).HasColumnName("instrument");

            b.HasOne(x => x.Album)
                .WithMany(a => a.Performers)
                .HasForeignKey(x => x.AlbumId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(x => x.Track)
                .WithMany(t => t.Performers)
                .HasForeignKey(x => x.TrackId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(x => x.Person)
                .WithMany(p => p.PerformerCredits)
                .HasForeignKey(x => x.PersonId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasOne(x => x.Ensemble)
                .WithMany(e => e.PerformerCredits)
                .HasForeignKey(x => x.EnsembleId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => new { x.AlbumId, x.TrackId, x.Position });
            b.HasIndex(x => x.PersonId);
            b.HasIndex(x => x.EnsembleId);
        });

        // The album_sessions table + AlbumSessionRow mapping were retired in
        // the session-as-fields refactor. Existing data is migrated up to the
        // album row and (per track) down to album_tracks by the schema
        // upgrade in SqliteCanonDataService.Migrations.cs; the table itself
        // is dropped at the end of the upgrade.
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pick lists
    // ─────────────────────────────────────────────────────────────────────────
    private static void ConfigurePickLists(ModelBuilder mb)
    {
        mb.Entity<PickListValueRow>(b =>
        {
            b.ToTable("pick_list_values");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.ListName).HasColumnName("list_name").IsRequired();
            b.Property(x => x.Position).HasColumnName("position");
            b.Property(x => x.Value).HasColumnName("value");
            b.Property(x => x.ValueJson).HasColumnName("value_json");

            b.HasIndex(x => new { x.ListName, x.Position }).IsUnique();
        });
    }
}
