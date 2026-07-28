using Microsoft.EntityFrameworkCore;
using TicketingPlatform.Domain;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Infrastructure.Outbox;
using TicketingPlatform.Infrastructure.ReadModels;

namespace TicketingPlatform.Infrastructure.Persistence;

public class TicketingDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public TicketingDbContext(DbContextOptions<TicketingDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// Referenced inside the global query filters below. EF builds the model once but evaluates
    /// this per query, so each request is scoped to its own tenant.
    /// </summary>
    public Guid? CurrentTenantId => _tenantContext.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<TicketType> TicketTypes => Set<TicketType>();
    public DbSet<Inventory> Inventories => Set<Inventory>();
    public DbSet<Hold> Holds => Set<Hold>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<EventAvailabilityView> EventAvailability => Set<EventAvailabilityView>();
    public DbSet<Ticket> Tickets => Set<Ticket>();

    // Phase A: venue geometry. Reusable across events, versioned immutably.
    public DbSet<Performance> Performances => Set<Performance>();
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<Hall> Halls => Set<Hall>();
    public DbSet<SeatMapVersion> SeatMapVersions => Set<SeatMapVersion>();
    public DbSet<Section> Sections => Set<Section>();
    public DbSet<SeatRow> SeatRows => Set<SeatRow>();
    public DbSet<Seat> Seats => Set<Seat>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Tenant: top-level owner, NOT tenant-scoped (no TenantId, no query filter).
        modelBuilder.Entity<Tenant>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.Name).IsRequired().HasMaxLength(200);
            b.Property(t => t.Slug).IsRequired().HasMaxLength(100);
            b.HasIndex(t => t.Slug).IsUnique();
            b.HasMany(t => t.Events)
                .WithOne()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Event>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.Name).IsRequired().HasMaxLength(200);
            b.Property(e => e.VenueName).HasMaxLength(200);
            b.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(e => e.Category).HasConversion<string>().HasMaxLength(20);
            b.Property(e => e.ImagePath).HasMaxLength(500);
            b.HasIndex(e => e.TenantId);
            // The marketplace catalog's scan path: OnSale + category + date ordering.
            b.HasIndex(e => new { e.Status, e.Category, e.StartsAt });
            b.HasMany(e => e.TicketTypes)
                .WithOne(tt => tt.Event)
                .HasForeignKey(tt => tt.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tenant isolation: every Event read is scoped to the current tenant.
            b.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<TicketType>(b =>
        {
            b.HasKey(tt => tt.Id);
            b.Property(tt => tt.Name).IsRequired().HasMaxLength(100);
            b.Property(tt => tt.Price).HasPrecision(18, 2);
            b.Property(tt => tt.Currency).IsRequired().HasMaxLength(3);
            b.HasIndex(tt => tt.TenantId);
            b.HasOne(tt => tt.Inventory)
                .WithOne(i => i.TicketType)
                .HasForeignKey<Inventory>(i => i.TicketTypeId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasQueryFilter(tt => tt.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Inventory>(b =>
        {
            b.HasKey(i => i.Id);
            b.HasIndex(i => i.TicketTypeId).IsUnique();
            b.HasIndex(i => i.TenantId);

            // Optimistic concurrency via Postgres system column xmin. Foundation for Phase 5.
            // Npgsql 10 removed UseXminAsConcurrencyToken(); declaring a uint "xmin" shadow property
            // with OnAddOrUpdate + IsConcurrencyToken triggers the same convention.
            b.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            b.HasQueryFilter(i => i.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Hold>(b =>
        {
            b.HasKey(h => h.Id);
            b.Property(h => h.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(h => h.TenantId);
            b.HasIndex(h => h.CustomerUserId);

            // Phase 5's hold-expiry background service scans "Active holds past their TTL";
            // this composite index makes that scan an index seek instead of a table walk.
            b.HasIndex(h => new { h.Status, h.ExpiresAt });

            // The reconciler scans PaymentPending holds whose lease has expired.
            b.HasIndex(h => new { h.Status, h.PaymentLeaseUntil });

            // Optimistic concurrency via Postgres xmin makes the Active -> PaymentPending claim
            // (and the PaymentPending -> Confirmed finalize) atomic: two racing writers cannot
            // both win. Same shadow-property pattern as Inventory.
            b.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            b.HasOne(h => h.TicketType)
                .WithMany()
                .HasForeignKey(h => h.TicketTypeId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasQueryFilter(h => h.TenantId == CurrentTenantId);
        });

        // Users are NOT tenant-filtered: login happens before a tenant is known, and customers
        // and platform admins have no tenant at all. Staff's tenant boundary is the tenant_id
        // claim in their JWT, not a filter here.
        modelBuilder.Entity<User>(b =>
        {
            b.HasKey(u => u.Id);
            b.Property(u => u.Email).IsRequired().HasMaxLength(256);
            b.Property(u => u.NormalizedEmail).IsRequired().HasMaxLength(256);
            b.Property(u => u.PasswordHash).IsRequired();
            b.Property(u => u.Role).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(u => u.NormalizedEmail).IsUnique(); // one account per email, platform-wide
        });

        modelBuilder.Entity<RefreshToken>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.TokenHash).IsRequired().HasMaxLength(64); // SHA-256 hex
            b.Property(t => t.FamilyId).IsRequired();
            b.HasIndex(t => t.TokenHash).IsUnique();                    // hash-based lookup path
            b.HasIndex(t => t.FamilyId);                                // family revocation path
            b.HasIndex(t => t.UserId);
            b.HasOne(t => t.User)
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Order>(b =>
        {
            b.HasKey(o => o.Id);
            b.Property(o => o.CustomerEmail).IsRequired().HasMaxLength(256);
            b.Property(o => o.Amount).HasPrecision(18, 2);
            b.Property(o => o.Currency).IsRequired().HasMaxLength(3);
            b.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(o => o.ProviderChargeId).HasMaxLength(100);
            b.Property(o => o.ProviderRefundId).HasMaxLength(100);
            b.Property(o => o.RefundInitiatedByActor).HasMaxLength(100);
            b.HasIndex(o => o.TenantId);
            b.HasIndex(o => o.CustomerUserId);

            // At most ONE live purchase lineage per hold: a PendingPayment/Confirmed/Refunded
            // order excludes any other. Declined (PaymentFailed) attempts are excluded from the
            // filter so a buyer can retry the same hold after a decline. This is the database's
            // backstop for "one successful order per hold", independent of the claim logic.
            b.HasIndex(o => o.HoldId)
                .IsUnique()
                .HasFilter("\"Status\" IN ('PendingPayment', 'Confirmed', 'RefundPending', 'Refunded')");

            // Optimistic concurrency: the confirm/refund transitions run through change tracking
            // guarded by this token, so a double-finalize (retry + reconciler) cannot both win.
            b.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            b.HasOne(o => o.Hold)
                .WithMany()
                .HasForeignKey(o => o.HoldId)
                .OnDelete(DeleteBehavior.Restrict); // orders are financial records - never cascade-delete

            b.HasQueryFilter(o => o.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Notification>(b =>
        {
            b.HasKey(n => n.Id);
            b.Property(n => n.Type).IsRequired().HasMaxLength(50);
            b.Property(n => n.Message).IsRequired().HasMaxLength(1000);
            b.HasIndex(n => n.TenantId);
            b.HasQueryFilter(n => n.TenantId == CurrentTenantId);
        });

        // Outbox plumbing: NOT tenant-filtered - the dispatcher and the consumer dedupe run in
        // background scopes with no tenant, and events already carry their tenant in the payload.
        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.HasKey(m => m.Id);
            b.Property(m => m.Type).IsRequired().HasMaxLength(100);
            b.Property(m => m.Payload).IsRequired();
            b.Property(m => m.SchemaVersion).HasDefaultValue(1);
            b.Property(m => m.CorrelationId).HasMaxLength(64);
            b.Property(m => m.LockedBy).HasMaxLength(100);
            b.Property(m => m.LastError).HasMaxLength(2000);
            b.HasIndex(m => new { m.ProcessedAt, m.OccurredAt }); // the dispatcher's poll path
            b.HasIndex(m => new { m.ProcessedAt, m.LockedUntil, m.OccurredAt });
            b.HasIndex(m => new { m.ProcessedAt, m.FailedAt, m.NextAttemptAt, m.LockedUntil, m.OccurredAt })
                .HasDatabaseName("IX_OutboxMessages_Dispatchable");
        });

        modelBuilder.Entity<ProcessedMessage>(b =>
        {
            b.HasKey(m => new { m.MessageId, m.Consumer }); // per-consumer dedupe (fan-out safe)
            b.Property(m => m.Consumer).HasMaxLength(100);
        });

        modelBuilder.Entity<Performance>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(p => p.TenantId);
            // The catalog's real scan path once dates exist: "what is playing, soonest first".
            b.HasIndex(p => new { p.EventId, p.StartsAt });
            b.HasIndex(p => new { p.Status, p.StartsAt });

            b.HasOne(p => p.Event)
                .WithMany(e => e.Performances)
                .HasForeignKey(p => p.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // Geometry is RESTRICTed, not cascaded: deleting a hall or a seat-map version out from
            // under a performance that sold seats against it would orphan the tickets' meaning.
            b.HasOne(p => p.Hall)
                .WithMany()
                .HasForeignKey(p => p.HallId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne(p => p.SeatMapVersion)
                .WithMany()
                .HasForeignKey(p => p.SeatMapVersionId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasQueryFilter(p => p.TenantId == CurrentTenantId);
        });

        // --- Phase A: venue geometry -----------------------------------------------------------
        // Tenant-owned like every other operational entity, so the same query filter (and the same
        // TenantScope) governs them. Nothing here references Event yet: this slice is additive, so
        // the existing Event -> TicketType -> Inventory path is untouched.

        modelBuilder.Entity<Venue>(b =>
        {
            b.HasKey(v => v.Id);
            b.Property(v => v.Name).IsRequired().HasMaxLength(200);
            b.Property(v => v.AddressLine).HasMaxLength(300);
            b.Property(v => v.City).HasMaxLength(120);
            b.Property(v => v.CountryCode).HasMaxLength(2);
            b.Property(v => v.TimeZoneId).HasMaxLength(64);
            b.HasIndex(v => v.TenantId);
            b.HasMany(v => v.Halls)
                .WithOne(h => h.Venue)
                .HasForeignKey(h => h.VenueId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(v => v.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Hall>(b =>
        {
            b.HasKey(h => h.Id);
            b.Property(h => h.Name).IsRequired().HasMaxLength(200);
            b.HasIndex(h => h.TenantId);
            b.HasMany(h => h.SeatMapVersions)
                .WithOne(s => s.Hall)
                .HasForeignKey(s => s.HallId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(h => h.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<SeatMapVersion>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Notes).HasMaxLength(500);
            b.HasIndex(s => s.TenantId);
            // Version numbers are the human-facing identity of a layout, so they must not repeat
            // within a hall - this is the database half of "a change publishes a NEW version".
            b.HasIndex(s => new { s.HallId, s.Version }).IsUnique();
            b.HasMany(s => s.Sections)
                .WithOne(sec => sec.SeatMapVersion)
                .HasForeignKey(sec => sec.SeatMapVersionId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Section>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Name).IsRequired().HasMaxLength(100);
            b.HasIndex(s => s.TenantId);
            b.HasMany(s => s.Rows)
                .WithOne(r => r.Section)
                .HasForeignKey(r => r.SectionId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<SeatRow>(b =>
        {
            b.HasKey(r => r.Id);
            b.Property(r => r.Label).IsRequired().HasMaxLength(20);
            b.HasIndex(r => r.TenantId);
            b.HasMany(r => r.Seats)
                .WithOne(s => s.SeatRow)
                .HasForeignKey(s => s.SeatRowId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Seat>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Number).IsRequired().HasMaxLength(20);
            b.Property(s => s.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(s => s.MapX).HasPrecision(9, 2);
            b.Property(s => s.MapY).HasPrecision(9, 2);
            b.HasIndex(s => s.TenantId);
            // Seat IDENTITY: one "12" per row. Geometry may move freely; the printed identity may not
            // collide, because that is what the door reads off the ticket.
            b.HasIndex(s => new { s.SeatRowId, s.Number }).IsUnique();
            b.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Ticket>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.Code).IsRequired().HasMaxLength(64);
            b.Property(t => t.FilePath).IsRequired().HasMaxLength(500);
            b.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(t => t.Code).IsUnique();
            b.HasIndex(t => t.OrderId).IsUnique(); // one issued document per order
            b.HasIndex(t => t.TenantId);

            // Optimistic concurrency: the Issued -> Scanned flip is a compare-and-swap, so two
            // scanners racing on one code produce exactly one admission.
            b.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            b.HasQueryFilter(t => t.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<IdempotencyRecord>(b =>
        {
            b.HasKey(r => r.Id);
            b.Property(r => r.ActorKey).IsRequired().HasMaxLength(100);
            b.Property(r => r.Key).IsRequired().HasMaxLength(200);
            b.Property(r => r.RequestHash).IsRequired().HasMaxLength(64);
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(r => new { r.TenantId, r.ActorKey, r.Key }).IsUnique();
            b.HasIndex(r => r.OrderId);
            b.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });

        // CQRS read model: tenant-filtered like every tenant-owned read; the projection
        // consumer (background, no tenant) uses IgnoreQueryFilters to upsert.
        modelBuilder.Entity<EventAvailabilityView>(b =>
        {
            b.HasKey(v => v.TicketTypeId);
            b.Property(v => v.EventName).IsRequired().HasMaxLength(200);
            b.Property(v => v.TicketTypeName).IsRequired().HasMaxLength(100);
            b.HasIndex(v => v.EventId);  // the query side reads per event
            b.HasIndex(v => v.TenantId);
            b.HasQueryFilter(v => v.TenantId == CurrentTenantId);
        });
    }
}
