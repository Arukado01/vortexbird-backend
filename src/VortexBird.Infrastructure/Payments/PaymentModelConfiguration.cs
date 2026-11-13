using Microsoft.EntityFrameworkCore;
using VortexBird.Domain.Entities;

namespace VortexBird.Infrastructure.Payments;

public static class PaymentModelConfiguration
{
    public static void ConfigurePayments(ModelBuilder b)
    {
        b.Entity<PaymentCheckout>().ToTable("PaymentCheckouts");
        b.Entity<PaymentCheckout>().HasKey(x => x.OrderId);
        b.Entity<Order>().HasAlternateKey(x => new { x.Id, x.PartnerId, x.ChannelId });
        b.Entity<PaymentCheckout>().HasOne<Order>().WithMany().HasForeignKey(x => new { x.OrderId, x.PartnerId, x.ChannelId }).HasPrincipalKey(x => new { x.Id, x.PartnerId, x.ChannelId }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PaymentCheckout>().HasIndex(x => x.ExternalReference).IsUnique();
        b.Entity<PaymentCheckout>().HasIndex(x => x.PreferenceId).IsUnique();
        b.Entity<PaymentCheckout>().HasIndex(x => new { x.NextAttemptAt, x.LeaseUntil });
        b.Entity<PaymentCheckout>().Property(x => x.ExpectedAmount).HasPrecision(18, 2);
        b.Entity<PaymentCheckout>().ToTable(t => t.HasCheckConstraint("ck_checkout_amount", "\"ExpectedAmount\" > 0 AND \"Currency\" = 'COP'"));
        b.Entity<PaymentInbox>().ToTable("PaymentInbox");
        b.Entity<PaymentInbox>().HasKey(x => x.Id);
        b.Entity<PaymentInbox>().HasIndex(x => x.NotificationKey).IsUnique();
        b.Entity<PaymentInbox>().HasIndex(x => new { x.Status, x.NextAttemptAt });
        b.Entity<PaymentReceipt>().ToTable("PaymentReceipts");
        b.Entity<PaymentReceipt>().HasKey(x => x.ProviderPaymentId);
        b.Entity<PaymentReceipt>().HasOne<PaymentCheckout>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PaymentReceipt>().HasIndex(x => new { x.OrderId, x.Disposition });
        b.Entity<Payment>().HasIndex(x => x.OrderId).IsUnique();
    }
}
