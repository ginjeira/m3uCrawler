using System;
using m3uCrawler.Services.Auth;
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
    public DbSet<AffinityGroupEntity> AffinityGroups => Set<AffinityGroupEntity>();
    public DbSet<AffinityMemberEntity> AffinityMembers => Set<AffinityMemberEntity>();
    public DbSet<DispatcharrChannelOwnershipEntity> DispatcharrChannelOwnerships => Set<DispatcharrChannelOwnershipEntity>();
    public DbSet<DispatcharrStreamOwnershipEntity> DispatcharrStreamOwnerships => Set<DispatcharrStreamOwnershipEntity>();
    public DbSet<ReviewItemEntity> ReviewItems => Set<ReviewItemEntity>();
    public DbSet<SyncRunEntity> SyncRuns => Set<SyncRunEntity>();
    public DbSet<PendingCountryApprovalEntity> PendingCountryApprovals => Set<PendingCountryApprovalEntity>();
    public DbSet<SourceEntity> Sources => Set<SourceEntity>();
    public DbSet<ChannelSourceEntity> ChannelSources => Set<ChannelSourceEntity>();
    public DbSet<OrderingListEntity> OrderingLists => Set<OrderingListEntity>();
    public DbSet<OrderingItemEntity> OrderingItems => Set<OrderingItemEntity>();
    public DbSet<SourcePriorityPolicyEntity> SourcePriorityPolicies => Set<SourcePriorityPolicyEntity>();
    public DbSet<ImportPolicyEntity> ImportPolicies => Set<ImportPolicyEntity>();
    public DbSet<CanonicalGroupEntity> CanonicalGroups => Set<CanonicalGroupEntity>();
    public DbSet<GroupMappingEntity> GroupMappings => Set<GroupMappingEntity>();
    public DbSet<MatchingAuditEntity> MatchingAudits => Set<MatchingAuditEntity>();
    public DbSet<ChannelSourceObservationEntity> ChannelSourceObservations => Set<ChannelSourceObservationEntity>();
    public DbSet<SyncRunStepEntity> SyncRunSteps => Set<SyncRunStepEntity>();
    public DbSet<ScheduledJobEntity> ScheduledJobs => Set<ScheduledJobEntity>();
    public DbSet<AdminUserEntity> AdminUsers => Set<AdminUserEntity>();
    public DbSet<AdminSessionEntity> AdminSessions => Set<AdminSessionEntity>();
    public DbSet<LiveRunEntity> LiveRuns => Set<LiveRunEntity>();
    public DbSet<LiveRunStepEntity> LiveRunSteps => Set<LiveRunStepEntity>();

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
            e.Property(x => x.Country).HasMaxLength(10);
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

        // AffinityGroup
        modelBuilder.Entity<AffinityGroupEntity>(e =>
        {
            e.ToTable("affinity_groups");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.CanonicalChannelKey).HasMaxLength(120);
            e.Property(x => x.CountryCode).HasMaxLength(10);
            e.Property(x => x.CanonicalChannelId).IsRequired(false);
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.Kind);
            e.HasIndex(x => x.CountryCode);
            e.HasIndex(x => x.CanonicalChannelKey);
            e.HasOne(x => x.CanonicalChannel)
                .WithMany()
                .HasForeignKey(x => x.CanonicalChannelId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Members)
                .WithOne(m => m.AffinityGroup)
                .HasForeignKey(m => m.AffinityGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AffinityMember
        modelBuilder.Entity<AffinityMemberEntity>(e =>
        {
            e.ToTable("affinity_members");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.NormalizedMember).IsRequired().HasMaxLength(200);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.AffinityGroupId).IsRequired();
            e.Property(x => x.CreatedAtUtc).IsRequired();
            // Unicidade APENAS entre Channel affinities: uma variante
            // não pode resolver para dois canais canónicos. Membros
            // Country não são restringidos (podem coexistir com a
            // mesma variante numa Channel affinity).
            e.HasIndex(x => x.NormalizedMember)
                .IsUnique()
                .HasDatabaseName("IX_affinity_members_NormalizedMember_Channel")
                .HasFilter("\"Kind\" = 0");
            e.HasOne(x => x.AffinityGroup)
                .WithMany(g => g.Members)
                .HasForeignKey(x => x.AffinityGroupId)
                .OnDelete(DeleteBehavior.Cascade);
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

        // PendingCountryApproval
        modelBuilder.Entity<PendingCountryApprovalEntity>(e =>
        {
            e.ToTable("pending_country_approvals");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.NormalizedIdentity).IsRequired().HasMaxLength(200);
            e.Property(x => x.OriginalTitle).IsRequired().HasMaxLength(500);
            e.Property(x => x.CountryCode).IsRequired().HasMaxLength(10);
            e.Property(x => x.StreamUrl).IsRequired().HasMaxLength(1000);
            e.Property(x => x.SourceGroup).HasMaxLength(200);
            e.Property(x => x.ReasonSignature).IsRequired().HasMaxLength(120);
            e.Property(x => x.State).HasConversion<int>();
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.Property(x => x.ResolvedAtUtc);
            e.HasIndex(x => x.NormalizedIdentity);
            e.HasIndex(x => x.CountryCode);
            e.HasIndex(x => x.State);
        });

        // PHASE 4 — Source
        modelBuilder.Entity<SourceEntity>(e =>
        {
            e.ToTable("sources");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.Key).IsRequired().HasMaxLength(120);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Origin).IsRequired().HasMaxLength(1000);
            e.Property(x => x.IsEnabled).IsRequired();
            e.Property(x => x.Priority).IsRequired();
            e.Property(x => x.LastDiscoveryAtUtc);
            e.Property(x => x.LastValidationAtUtc);
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.HasIndex(x => x.Key).IsUnique();
            e.HasMany(x => x.ChannelSources)
                .WithOne(cs => cs.Source)
                .HasForeignKey(cs => cs.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // PHASE 4 — ChannelSource
        modelBuilder.Entity<ChannelSourceEntity>(e =>
        {
            e.ToTable("channel_sources");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.CanonicalChannelId).IsRequired();
            e.Property(x => x.SourceId).IsRequired();
            e.Property(x => x.StreamUrl).IsRequired().HasMaxLength(1000);
            e.Property(x => x.ExternalStreamId).HasMaxLength(200);
            e.Property(x => x.Quality).HasConversion<int>();
            e.Property(x => x.Epg).HasConversion<int>();
            e.Property(x => x.Availability).HasConversion<int>();
            e.Property(x => x.MatchConfidence).IsRequired();
            e.Property(x => x.MatchMethod).IsRequired().HasMaxLength(80);
            e.Property(x => x.FirstSeenAtUtc).IsRequired();
            e.Property(x => x.LastSeenAtUtc).IsRequired();
            e.Property(x => x.LastTestedAtUtc).IsRequired();
            e.Property(x => x.LastResponseTimeMs).IsRequired();
            e.Property(x => x.IsEnabled).IsRequired();
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.HasIndex(x => new { x.CanonicalChannelId, x.SourceId });
            e.HasIndex(x => x.SourceId);
            e.HasOne(x => x.CanonicalChannel)
                .WithMany()
                .HasForeignKey(x => x.CanonicalChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // PHASE 5 — OrderingList
        modelBuilder.Entity<OrderingListEntity>(e =>
        {
            e.ToTable("ordering_lists");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.Key).IsRequired().HasMaxLength(120);
            e.Property(x => x.Country).HasMaxLength(10);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.IsEnabled).IsRequired();
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.HasIndex(x => x.Key).IsUnique();
            e.HasMany(x => x.Items)
                .WithOne(i => i.OrderingList)
                .HasForeignKey(i => i.OrderingListId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // PHASE 5 — OrderingItem
        modelBuilder.Entity<OrderingItemEntity>(e =>
        {
            e.ToTable("ordering_items");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.OrderingListId).IsRequired();
            e.Property(x => x.CanonicalChannelId).IsRequired();
            e.Property(x => x.Position).IsRequired();
            e.Property(x => x.IsEnabled).IsRequired();
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.HasIndex(x => new { x.OrderingListId, x.Position }).IsUnique();
            e.HasIndex(x => new { x.OrderingListId, x.CanonicalChannelId }).IsUnique();
            e.HasOne(x => x.CanonicalChannel)
                .WithMany()
                .HasForeignKey(x => x.CanonicalChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // PHASE 6 — SourcePriorityPolicy
        modelBuilder.Entity<SourcePriorityPolicyEntity>(e =>
        {
            e.ToTable("source_priority_policies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Scope).IsRequired().HasMaxLength(60);
            e.Property(x => x.CanonicalChannelId);
            e.Property(x => x.CriteriaJson).IsRequired();
            e.Property(x => x.PreferredQuality).HasMaxLength(40);
            e.Property(x => x.AllowFallback).IsRequired();
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.HasIndex(x => x.Scope).IsUnique();
        });

        // PHASE 8 — ImportPolicy
        modelBuilder.Entity<ImportPolicyEntity>(e =>
        {
            e.ToTable("import_policies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.MediaKind).HasConversion<int>();
            e.Property(x => x.VodPolicy).HasConversion<int>();
            e.Property(x => x.TargetGroupsCsv).HasMaxLength(2000);
            e.Property(x => x.ExcludedGroupsCsv).HasMaxLength(2000);
            e.Property(x => x.IsEnabled).IsRequired();
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.HasIndex(x => x.MediaKind).IsUnique();
        });

        // PHASE 8 — CanonicalGroup
        modelBuilder.Entity<CanonicalGroupEntity>(e =>
        {
            e.ToTable("canonical_groups");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Key).IsRequired().HasMaxLength(120);
            e.Property(x => x.DisplayName).IsRequired().HasMaxLength(200);
            e.Property(x => x.Country).HasMaxLength(10);
            e.Property(x => x.Order).IsRequired();
            e.Property(x => x.IsEnabled).IsRequired();
            e.Property(x => x.IsDefault).IsRequired();
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.HasIndex(x => x.Key).IsUnique();
        });

        // PHASE 8 — GroupMapping
        modelBuilder.Entity<GroupMappingEntity>(e =>
        {
            e.ToTable("group_mappings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.SourceKind).HasConversion<int>();
            e.Property(x => x.SourceGroupTitle).IsRequired().HasMaxLength(400);
            e.Property(x => x.CanonicalGroupId).IsRequired();
            e.Property(x => x.IsEnabled).IsRequired();
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.HasIndex(x => new { x.SourceKind, x.SourceGroupTitle }).IsUnique();
            e.HasOne(x => x.CanonicalGroup)
                .WithMany()
                .HasForeignKey(x => x.CanonicalGroupId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // PHASE 3 — MatchingAudit
        modelBuilder.Entity<MatchingAuditEntity>(e =>
        {
            e.ToTable("matching_audits");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.NormalizedIdentity).IsRequired().HasMaxLength(200);
            e.Property(x => x.OriginalTitle).IsRequired().HasMaxLength(500);
            e.Property(x => x.SourceGroup).HasMaxLength(200);
            e.Property(x => x.ResolutionKind).IsRequired().HasMaxLength(40);
            e.Property(x => x.ReasonSignature).IsRequired().HasMaxLength(120);
            e.Property(x => x.Confidence).IsRequired();
            e.Property(x => x.AtUtc).IsRequired();
            e.HasIndex(x => x.AtUtc);
            e.HasIndex(x => x.CanonicalChannelId);
            e.HasIndex(x => x.NormalizedIdentity);
        });

        // PHASE 9 b — ChannelSourceObservation (history)
        modelBuilder.Entity<ChannelSourceObservationEntity>(e =>
        {
            e.ToTable("channel_source_observations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.ChannelSourceId).IsRequired();
            e.Property(x => x.Quality).HasConversion<int>();
            e.Property(x => x.Epg).HasConversion<int>();
            e.Property(x => x.Availability).HasConversion<int>();
            e.Property(x => x.ResponseTimeMs).IsRequired();
            e.Property(x => x.ObservedAtUtc).IsRequired();
            e.HasIndex(x => new { x.ChannelSourceId, x.ObservedAtUtc });
        });

        // PHASE 11 — SyncRunStep
        modelBuilder.Entity<SyncRunStepEntity>(e =>
        {
            e.ToTable("sync_run_steps");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.SyncRunId).IsRequired();
            e.Property(x => x.Step).IsRequired().HasMaxLength(80);
            e.Property(x => x.StartedAtUtc).IsRequired();
            e.Property(x => x.FinishedAtUtc).IsRequired();
            e.Property(x => x.DurationMs).IsRequired();
            e.Property(x => x.ItemsProcessed).IsRequired();
            e.Property(x => x.ItemsSucceeded).IsRequired();
            e.Property(x => x.ItemsFailed).IsRequired();
            e.Property(x => x.Result).IsRequired().HasMaxLength(40);
            e.HasIndex(x => new { x.SyncRunId, x.Step }).IsUnique();
        });

        // PHASE 12 — ScheduledJob
        modelBuilder.Entity<ScheduledJobEntity>(e =>
        {
            e.ToTable("scheduled_jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Name).IsRequired().HasMaxLength(120);
            e.Property(x => x.CronExpression).IsRequired().HasMaxLength(80);
            e.Property(x => x.ActionName).IsRequired().HasMaxLength(80);
            e.Property(x => x.IsEnabled).IsRequired();
            e.Property(x => x.LastResult).HasMaxLength(120);
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
        });

        // PHASE 9C.2 — AdminUser
        modelBuilder.Entity<AdminUserEntity>(e =>
        {
            e.ToTable("admin_users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Username).IsRequired().HasMaxLength(64);
            e.Property(x => x.PasswordHash).IsRequired().HasMaxLength(256);
            e.Property(x => x.IsEnabled).IsRequired();
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.HasIndex(x => x.Username).IsUnique();
        });

        // PHASE 9C.2 — AdminSession
        modelBuilder.Entity<AdminSessionEntity>(e =>
        {
            e.ToTable("admin_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.SessionId).IsRequired().HasMaxLength(128);
            e.Property(x => x.AdminUserId).IsRequired();
            e.Property(x => x.CsrfToken).IsRequired().HasMaxLength(128);
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.ExpiresAtUtc).IsRequired();
            e.Property(x => x.LastSeenAtUtc).IsRequired();
            e.HasIndex(x => x.SessionId).IsUnique();
            e.HasIndex(x => x.AdminUserId);
            e.HasOne<AdminUserEntity>()
                .WithMany()
                .HasForeignKey(x => x.AdminUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // PHASE 9C.4 — LiveRun (Telegram cycle)
        modelBuilder.Entity<LiveRunEntity>(e =>
        {
            e.ToTable("live_run_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.RunId).IsRequired().HasMaxLength(64);
            e.Property(x => x.Mode).IsRequired().HasMaxLength(32);
            e.Property(x => x.Source).IsRequired().HasMaxLength(32);
            e.Property(x => x.StartedAtUtc).IsRequired();
            e.Property(x => x.FinishedAtUtc);
            e.Property(x => x.TerminalStatus).HasConversion<int>();
            e.Property(x => x.LastMessage).HasMaxLength(200);
            e.Property(x => x.CountsJson).IsRequired().HasMaxLength(4000);
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.HasIndex(x => x.RunId).IsUnique();
            e.HasIndex(x => x.StartedAtUtc);
            e.HasIndex(x => x.FinishedAtUtc);
            e.HasMany(x => x.Steps)
                .WithOne(s => s.LiveRun)
                .HasForeignKey(s => s.LiveRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // PHASE 9C.4 — LiveRunStep
        modelBuilder.Entity<LiveRunStepEntity>(e =>
        {
            e.ToTable("live_run_steps");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.LiveRunId).IsRequired();
            e.Property(x => x.Phase).HasConversion<int>();
            e.Property(x => x.PhaseIndex).IsRequired();
            e.Property(x => x.PhaseStartedAtUtc).IsRequired();
            e.Property(x => x.PhaseFinishedAtUtc);
            e.Property(x => x.Message).HasMaxLength(200);
            e.Property(x => x.Result).IsRequired().HasMaxLength(40);
            e.HasIndex(x => new { x.LiveRunId, x.PhaseIndex }).IsUnique();
            e.HasIndex(x => new { x.LiveRunId, x.Phase });
        });
    }
}
