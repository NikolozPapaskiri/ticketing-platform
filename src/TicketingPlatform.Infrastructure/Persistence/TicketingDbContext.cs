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
    public DbSet<EventAvailabilityView> EventAvailability => Set<EventAvailabilityView>();

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
            b.HasIndex(e => e.TenantId);
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

            // Phase 5's hold-expiry background service scans "Active holds past their TTL";
            // this composite index makes that scan an index seek instead of a table walk.
            b.HasIndex(h => new { h.Status, h.ExpiresAt });

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
            b.HasIndex(t => t.TokenHash).IsUnique();                    // hash-based lookup path
            b.HasIndex(t => t.UserId);                                  // family revocation path
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
            b.HasIndex(o => o.TenantId);
            b.HasIndex(o => o.HoldId); // non-unique: a declined order can be retried on the same hold
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
            b.HasIndex(m => new { m.ProcessedAt, m.OccurredAt }); // the dispatcher's poll path
        });

        modelBuilder.Entity<ProcessedMessage>(b =>
        {
            b.HasKey(m => m.MessageId); // PK IS the dedupe check
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
