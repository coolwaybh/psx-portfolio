using Microsoft.EntityFrameworkCore;
using Psx.Api.Entities;

namespace Psx.Api.Data;

public class PsxDbContext(DbContextOptions<PsxDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();

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
            b.HasOne(s => s.User)
                .WithOne(u => u.Settings)
                .HasForeignKey<UserSettings>(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
