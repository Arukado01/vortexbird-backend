using Microsoft.EntityFrameworkCore;
using VortexBird.Domain.Entities;

namespace VortexBird.Infrastructure;

public sealed class IntegrationDbContext(DbContextOptions<IntegrationDbContext> options) : DbContext(options)
{
    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<OmsReplica> OmsReplicas => Set<OmsReplica>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Stock> Stocks => Set<Stock>();
    public DbSet<Price> Prices => Set<Price>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderHistory> OrderHistories => Set<OrderHistory>();
    public DbSet<InventoryReservation> InventoryReservations => Set<InventoryReservation>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<IntegrationExecution> IntegrationExecutions => Set<IntegrationExecution>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Partner>().HasIndex(x => x.Code).IsUnique();
        b.Entity<Customer>().HasIndex(x => new { x.PartnerId, x.ExternalId }).IsUnique();
        b.Entity<Customer>().HasOne<Partner>().WithMany().HasForeignKey(x => x.PartnerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OmsReplica>().HasIndex(x => x.OrderId).IsUnique();
        b.Entity<OmsReplica>().HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OmsReplica>().Property(x => x.LinesJson).HasColumnType("jsonb");
        b.Entity<Channel>().HasIndex(x => new { x.PartnerId, x.Code }).IsUnique();
        b.Entity<Channel>().HasAlternateKey(x => new { x.Id, x.PartnerId });
        b.Entity<Channel>().HasOne<Partner>().WithMany().HasForeignKey(x => x.PartnerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Product>().HasIndex(x => x.Sku).IsUnique();
        b.Entity<Stock>().HasIndex(x => new { x.PartnerId, x.ChannelId, x.StoreId, x.Sku }).IsUnique();
        b.Entity<Stock>().HasOne<Channel>().WithMany().HasForeignKey(x => new { x.ChannelId, x.PartnerId }).HasPrincipalKey(x => new { x.Id, x.PartnerId }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Price>().HasOne<Channel>().WithMany().HasForeignKey(x => new { x.ChannelId, x.PartnerId }).HasPrincipalKey(x => new { x.Id, x.PartnerId }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Order>().HasOne<Channel>().WithMany().HasForeignKey(x => new { x.ChannelId, x.PartnerId }).HasPrincipalKey(x => new { x.Id, x.PartnerId }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Stock>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<Stock>().ToTable(t => t.HasCheckConstraint("ck_stock", "\"OnHand\" >= 0 AND \"Reserved\" >= 0 AND \"Reserved\" <= \"OnHand\""));
        b.Entity<Price>().HasIndex(x => new { x.PartnerId, x.ChannelId, x.Sku, x.CustomerId }).IsUnique().AreNullsDistinct(false);
        b.Entity<Price>().Property(x => x.Amount).HasPrecision(18, 2);
        b.Entity<Order>().HasIndex(x => new { x.PartnerId, x.CreatedAt });
        b.Entity<Order>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<Order>().Property(x => x.Total).HasPrecision(18, 2);
        b.Entity<Order>().HasMany(x => x.Items).WithOne().HasForeignKey(x => x.OrderId);
        b.Entity<Order>().HasMany(x => x.Timeline).WithOne().HasForeignKey(x => x.OrderId);
        b.Entity<OrderItem>().Property(x => x.UnitPrice).HasPrecision(18, 2);
        b.Entity<InventoryReservation>().HasIndex(x => new { x.OrderId, x.StockId }).IsUnique();
        b.Entity<InventoryReservation>().HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId);
        b.Entity<InventoryReservation>().HasOne<Stock>().WithMany().HasForeignKey(x => x.StockId);
        b.Entity<Payment>().Property(x => x.Amount).HasPrecision(18, 2);
        b.Entity<Payment>().HasIndex(x => x.Reference).IsUnique();
        b.Entity<Payment>().HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId);
        b.Entity<IdempotencyRecord>().HasIndex(x => new { x.PartnerId, x.Key }).IsUnique();
        b.Entity<OutboxMessage>().HasIndex(x => new { x.Status, x.NextAttemptAt });
        b.Entity<ProcessedMessage>().HasKey(x => new { x.Id, x.Consumer });
        b.Entity<ActivityLog>().HasIndex(x => new { x.PartnerId, x.CreatedAt });
        b.Entity<ActivityLog>().HasIndex(x => new { x.UserId, x.CreatedAt });
        b.Entity<User>().HasIndex(x => x.Subject).IsUnique();
        b.Entity<User>().HasIndex(x => x.Email).IsUnique();
        b.Entity<Role>().HasIndex(x => x.Name).IsUnique();
        b.Entity<Permission>().HasIndex(x => x.Name).IsUnique();
        b.Entity<UserRole>().HasKey(x => new { x.UserId, x.RoleId });
        b.Entity<UserRole>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        b.Entity<UserRole>().HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId);
        b.Entity<RolePermission>().HasKey(x => new { x.RoleId, x.PermissionId });
        b.Entity<RolePermission>().HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId);
        b.Entity<RolePermission>().HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionId);
        b.Entity<WebhookSubscription>().HasOne<Partner>().WithMany().HasForeignKey(x => x.PartnerId);
        b.Entity<WebhookDelivery>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<WebhookDelivery>().HasIndex(x => new { x.SubscriptionId, x.EventId }).IsUnique();
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties().Where(p => p.ClrType == typeof(string)))
                property.SetMaxLength(property.Name is "Payload" or "ResponseJson" ? 65536 : 2048);
    }

    private void CheckAudit()
    {
        if (ChangeTracker.Entries<ActivityLog>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit entries are append-only.");
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess) { CheckAudit(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) { CheckAudit(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
}
