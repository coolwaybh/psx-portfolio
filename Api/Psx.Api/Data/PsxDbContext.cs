using Microsoft.EntityFrameworkCore;
using Psx.Api.Entities;

namespace Psx.Api.Data;

public class PsxDbContext(DbContextOptions<PsxDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<CashEntry> CashEntries => Set<CashEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.HasIndex(u => u.Username).IsUnique();
        });

        modelBuilder.Entity<LedgerEntry>(b =>
        {
            b.Property(t => t.Type).HasConversion<string>().HasMaxLength(4);
            b.Property(t => t.Shares).HasColumnType("decimal(18,4)");
            b.Property(t => t.Price).HasColumnType("decimal(18,4)");
            b.Property(t => t.Commission).HasColumnType("decimal(18,4)");
            b.HasIndex(t => new { t.UserId, t.Symbol });
            b.HasOne(t => t.User)
                .WithMany(u => u.LedgerEntries)
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserSettings>(b =>
        {
            b.HasKey(s => s.UserId);
            b.Property(s => s.DividendTaxRatePct).HasColumnType("decimal(5,2)");
            b.Property(s => s.OwnerName).HasMaxLength(100);
            b.HasOne(s => s.User)
                .WithOne(u => u.Settings)
                .HasForeignKey<UserSettings>(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CashEntry>(b =>
        {
            b.Property(c => c.Type).HasConversion<string>().HasMaxLength(10);
            b.Property(c => c.Amount).HasColumnType("decimal(18,4)");
            b.Property(c => c.GrossAmount).HasColumnType("decimal(18,4)");
            b.Property(c => c.TaxRatePct).HasColumnType("decimal(5,2)");
            b.Property(c => c.Symbol).HasMaxLength(20);
            b.HasIndex(c => c.UserId);
            b.HasOne(c => c.User)
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            // Self-referencing pair link (dividend <-> its offsetting "direct to bank"
            // withdrawal). Must be NoAction, not Cascade - SQL Server rejects cascading
            // self-references outright at migration-apply time (error 1785).
            b.HasOne(c => c.LinkedEntry)
                .WithMany()
                .HasForeignKey(c => c.LinkedEntryId)
                .OnDelete(DeleteBehavior.NoAction);
            // Link to the LedgerEntry that auto-created this entry (buy/sell -> cash).
            // NoAction, not Cascade, for the same reason as above - CashEntries already
            // cascades from Users directly, and Users also cascades to LedgerEntries, so
            // a DB-level cascade here would be a second path to the same table (SQL
            // Server error 1785). Deletion is handled explicitly in the ledger DELETE
            // endpoint instead.
            b.HasOne(c => c.LedgerEntry)
                .WithMany()
                .HasForeignKey(c => c.LedgerEntryId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }
}
