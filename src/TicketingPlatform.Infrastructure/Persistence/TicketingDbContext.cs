using Microsoft.EntityFrameworkCore;
using TicketingPlatform.Domain;
using TicketingPlatform.Application.Abstractions;

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
    }
}
