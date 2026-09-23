using Microsoft.EntityFrameworkCore;
using Psx.Api.Entities;

namespace Psx.Api.Data;

public class PsxDbContext(DbContextOptions<PsxDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<CashEntry> CashEntries => Set<CashEntry>();
    public DbSet<EodPrice> EodPrices => Set<EodPrice>();
    public DbSet<FundamentalView> FundamentalViews => Set<FundamentalView>();
    public DbSet<MutualFund> MutualFunds => Set<MutualFund>();
    public DbSet<FundTransaction> FundTransactions => Set<FundTransaction>();
    public DbSet<FundNavHistory> FundNavHistories => Set<FundNavHistory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.Property(u => u.Username).HasMaxLength(50);
            b.HasIndex(u => u.Username).IsUnique();
        });

        modelBuilder.Entity<LedgerEntry>(b =>
        {
            b.Property(t => t.Type).HasConversion<string>().HasMaxLength(10);
            b.Property(t => t.Shares).HasColumnType("decimal(18,4)");
            b.Property(t => t.Price).HasColumnType("decimal(18,4)");
            b.Property(t => t.Commission).HasColumnType("decimal(18,4)");
            b.Property(t => t.NccplCharge).HasColumnType("decimal(18,4)");
            b.Property(t => t.Notes).HasMaxLength(1000);
            b.Property(t => t.Symbol).HasMaxLength(20);
            b.Property(t => t.Sector).HasMaxLength(50);
            b.Property(t => t.SplitRatioTo).HasColumnType("decimal(18,4)");
            b.Property(t => t.SplitRatioFrom).HasColumnType("decimal(18,4)");
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
            b.Property(s => s.CgtRatePct).HasColumnType("decimal(5,2)");
            // decimal(7,4), not (5,2) like the rates above - this rate is a fraction of a
            // percent (0.007%), and (5,2) would round it straight to 0.01.
            b.Property(s => s.NccplChargeRatePct).HasColumnType("decimal(7,4)");
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
            b.Property(c => c.CgtAmount).HasColumnType("decimal(18,4)");
            b.Property(c => c.CdcHoldAmount).HasColumnType("decimal(18,4)");
            b.Property(c => c.Symbol).HasMaxLength(20);
            b.Property(c => c.Notes).HasMaxLength(1000);
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

        modelBuilder.Entity<EodPrice>(b =>
        {
            b.Property(p => p.Symbol).HasMaxLength(20);
            b.Property(p => p.Close).HasColumnType("decimal(18,4)");
            b.HasIndex(p => new { p.Symbol, p.Date }).IsUnique();
        });

        modelBuilder.Entity<FundamentalView>(b =>
        {
            b.HasKey(f => f.Symbol);
            b.Property(f => f.Symbol).HasMaxLength(20);
            b.Property(f => f.Signal).HasMaxLength(20);
            b.Property(f => f.Confidence).HasMaxLength(20);
            b.Property(f => f.Note).HasMaxLength(2000);
        });

        modelBuilder.Entity<MutualFund>(b =>
        {
            b.Property(f => f.Name).HasMaxLength(200);
            b.Property(f => f.Amc).HasMaxLength(100);
            b.Property(f => f.Category).HasMaxLength(50);
            b.Property(f => f.CurrentNav).HasColumnType("decimal(18,4)");
            b.HasIndex(f => f.UserId);
            b.HasOne(f => f.User)
                .WithMany()
                .HasForeignKey(f => f.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FundTransaction>(b =>
        {
            b.Property(t => t.Type).HasConversion<string>().HasMaxLength(20);
            b.Property(t => t.Units).HasColumnType("decimal(18,4)");
            b.Property(t => t.Nav).HasColumnType("decimal(18,4)");
            b.Property(t => t.Amount).HasColumnType("decimal(18,4)");
            b.Property(t => t.FrontLoadPct).HasColumnType("decimal(5,2)");
            b.Property(t => t.BackLoadPct).HasColumnType("decimal(5,2)");
            b.Property(t => t.CgtAmount).HasColumnType("decimal(18,4)");
            b.Property(t => t.Notes).HasMaxLength(1000);
            b.HasIndex(t => new { t.UserId, t.FundId });
            b.HasOne(t => t.User)
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            // NoAction, not Cascade - FundTransactions already cascades from Users
            // directly above, and Users -> MutualFunds -> FundTransactions would be a
            // second cascade path to the same table (SQL Server error 1785), same
            // reasoning as LedgerEntry's link on CashEntry.
            b.HasOne(t => t.Fund)
                .WithMany()
                .HasForeignKey(t => t.FundId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<FundNavHistory>(b =>
        {
            b.Property(h => h.Nav).HasColumnType("decimal(18,4)");
            b.HasIndex(h => new { h.FundId, h.AsOfDate }).IsUnique();
            // Cascade is fine here (unlike FundTransaction above) - FundNavHistory only
            // has this one path down from User (via Fund), no second path to collide with.
            b.HasOne(h => h.Fund)
                .WithMany()
                .HasForeignKey(h => h.FundId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
