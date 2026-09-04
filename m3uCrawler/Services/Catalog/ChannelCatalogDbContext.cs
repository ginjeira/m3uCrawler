using System;
using Microsoft.EntityFrameworkCore;

namespace m3uCrawler.Services.Catalog;

public sealed class ChannelCatalogDbContext : DbContext
{
    public ChannelCatalogDbContext(DbContextOptions<ChannelCatalogDbContext> options)
        : base(options)
    {
    }

    public DbSet<CanonicalChannelEntity> CanonicalChannels => Set<CanonicalChannelEntity>();
    public DbSet<ChannelAliasEntity> ChannelAliases => Set<ChannelAliasEntity>();
    public DbSet<IdentityRuleEntity> IdentityRules => Set<IdentityRuleEntity>();
    public DbSet<DispatcharrChannelOwnershipEntity> DispatcharrChannelOwnerships => Set<DispatcharrChannelOwnershipEntity>();
    public DbSet<DispatcharrStreamOwnershipEntity> DispatcharrStreamOwnerships => Set<DispatcharrStreamOwnershipEntity>();
    public DbSet<ReviewItemEntity> ReviewItems => Set<ReviewItemEntity>();
    public DbSet<SyncRunEntity> SyncRuns => Set<SyncRunEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // CanonicalChannel
        modelBuilder.Entity<CanonicalChannelEntity>(e =>
        {
            e.ToTable("canonical_channels");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Key).IsRequired().HasMaxLength(120);
            e.Property(x => x.DisplayName).IsRequired().HasMaxLength(200);
            e.Property(x => x.PublicationPolicy).HasConversion<int>();
            e.Property(x => x.EditorialCategory).HasConversion<int>();
            e.Property(x => x.EditorialGroup).HasConversion<int>();
            e.Property(x => x.IsEnabled).IsRequired();
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.HasIndex(x => x.Key).IsUnique();
            e.HasMany(x => x.Aliases)
                .WithOne(a => a.CanonicalChannel)
                .HasForeignKey(a => a.CanonicalChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ChannelAlias
        modelBuilder.Entity<ChannelAliasEntity>(e =>
        {
            e.ToTable("channel_aliases");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.NormalizedAlias).IsRequired().HasMaxLength(200);
            e.Property(x => x.CanonicalChannelId).IsRequired();
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.HasIndex(x => x.NormalizedAlias).IsUnique();
        });

        // IdentityRule
        modelBuilder.Entity<IdentityRuleEntity>(e =>
        {
            e.ToTable("identity_rules");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.NormalizedIdentity).IsRequired().HasMaxLength(200);
            e.Property(x => x.Disposition).HasConversion<int>();
            e.Property(x => x.Reason).IsRequired().HasMaxLength(500);
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.HasIndex(x => x.NormalizedIdentity).IsUnique();
        });

        // DispatcharrChannelOwnership
        modelBuilder.Entity<DispatcharrChannelOwnershipEntity>(e =>
        {
            e.ToTable("dispatcharr_channel_ownerships");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.DispatcharrChannelId).IsRequired();
            e.Property(x => x.Ownership).HasConversion<int>();
            e.Property(x => x.FirstObservedAtUtc).IsRequired();
            e.Property(x => x.LastObservedAtUtc).IsRequired();
            e.Property(x => x.Evidence).IsRequired().HasMaxLength(500);
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.HasIndex(x => x.DispatcharrChannelId).IsUnique();
            e.HasOne(x => x.CanonicalChannel)
                .WithMany()
                .HasForeignKey(x => x.CanonicalChannelId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // DispatcharrStreamOwnership
        modelBuilder.Entity<DispatcharrStreamOwnershipEntity>(e =>
        {
            e.ToTable("dispatcharr_stream_ownerships");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.DispatcharrStreamId).IsRequired();
            e.Property(x => x.DispatcharrChannelId).IsRequired();
            e.Property(x => x.Ownership).HasConversion<int>();
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.HasIndex(x => x.DispatcharrStreamId).IsUnique();
        });

        // ReviewItem
        modelBuilder.Entity<ReviewItemEntity>(e =>
        {
            e.ToTable("review_items");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Fingerprint).IsRequired().HasMaxLength(64);
            e.Property(x => x.NormalizedIdentity).IsRequired().HasMaxLength(200);
            e.Property(x => x.SourceGroup).IsRequired().HasMaxLength(200);
            e.Property(x => x.ReasonSignature).IsRequired().HasMaxLength(120);
            e.Property(x => x.State).HasConversion<int>();
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.Property(x => x.ResolvedAtUtc);
            e.HasIndex(x => x.Fingerprint).IsUnique();
            e.HasOne(x => x.ApprovedCanonicalChannel)
                .WithMany()
                .HasForeignKey(x => x.ApprovedCanonicalChannelId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // SyncRun
        modelBuilder.Entity<SyncRunEntity>(e =>
        {
            e.ToTable("sync_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.StartedAtUtc).IsRequired();
            e.Property(x => x.FinishedAtUtc).IsRequired();
            e.Property(x => x.AppVersion).IsRequired().HasMaxLength(40);
            e.Property(x => x.Result).IsRequired().HasMaxLength(200);
        });
    }
}
