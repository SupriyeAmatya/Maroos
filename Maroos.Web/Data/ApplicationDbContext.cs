using Maroos.Core.Auctions;
using Maroos.Core.Decks;
using Maroos.Core.Scryfall;
using Maroos.Core.Watchlist;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Maroos.Web.Data;

/// <summary>
/// The app's database. Its migrations (Data/Migrations) are for SQLite; <see cref="PostgresDbContext"/> has the
/// same model with its own PostgreSQL migrations.
/// </summary>
public class ApplicationDbContext : IdentityDbContext<ApplicationUser>, IDataProtectionKeyContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }

    /// <summary>For provider-specific subclasses such as <see cref="PostgresDbContext"/>.</summary>
    protected ApplicationDbContext(DbContextOptions options) : base(options)
    {
    }

    public DbSet<CachedCard> CachedCards => Set<CachedCard>();
    public DbSet<Deck> Decks => Set<Deck>();
    public DbSet<DeckCard> DeckCards => Set<DeckCard>();
    public DbSet<Auction> Auctions => Set<Auction>();
    public DbSet<Bid> Bids => Set<Bid>();
    public DbSet<Watch> Watches => Set<Watch>();

    /// <summary>ASP.NET Core Data Protection keys (they sign login cookies), so they survive redeploys.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(ApplicationUser.MaxDisplayNameLength);
        });

        builder.Entity<CachedCard>(card =>
        {
            card.HasKey(c => c.ScryfallId);
            card.Property(c => c.ScryfallId).ValueGeneratedNever();
            card.Property(c => c.Name).HasMaxLength(200);
            card.HasIndex(c => c.Name);
        });

        builder.Entity<Deck>(deck =>
        {
            deck.HasKey(d => d.Id);
            deck.Property(d => d.Name).HasMaxLength(Deck.MaxNameLength);
            deck.Property(d => d.Format).HasMaxLength(40);
            deck.HasOne<ApplicationUser>().WithMany().HasForeignKey(d => d.OwnerId).OnDelete(DeleteBehavior.Cascade);
            deck.HasMany(d => d.Cards).WithOne().HasForeignKey(c => c.DeckId).OnDelete(DeleteBehavior.Cascade);
            deck.HasIndex(d => new { d.OwnerId, d.UpdatedAt });
        });

        builder.Entity<DeckCard>(card =>
        {
            card.HasKey(c => new { c.DeckId, c.ScryfallId, c.Board });
            card.Property(c => c.CardName).HasMaxLength(200);
            card.Property(c => c.SetCode).HasMaxLength(10);
            card.Property(c => c.CollectorNumber).HasMaxLength(20);
        });

        builder.Entity<Auction>(auction =>
        {
            auction.HasKey(a => a.Id);
            auction.HasOne<ApplicationUser>().WithMany().HasForeignKey(a => a.SellerId).OnDelete(DeleteBehavior.Cascade);
            auction.HasOne<ApplicationUser>().WithMany().HasForeignKey(a => a.WinnerId).OnDelete(DeleteBehavior.SetNull);
            auction.Property(a => a.CardName).HasMaxLength(200);
            auction.Property(a => a.SetCode).HasMaxLength(10);
            auction.Property(a => a.SetName).HasMaxLength(100);
            auction.Property(a => a.CollectorNumber).HasMaxLength(20);
            auction.Property(a => a.PhotoPath).HasMaxLength(200);
            auction.HasIndex(a => new { a.Status, a.EndsAtUtc });
            auction.HasIndex(a => a.SellerId);
        });

        builder.Entity<Bid>(bid =>
        {
            bid.HasKey(b => b.Id);
            bid.HasOne<Auction>().WithMany().HasForeignKey(b => b.AuctionId).OnDelete(DeleteBehavior.Cascade);
            bid.HasOne<ApplicationUser>().WithMany().HasForeignKey(b => b.BidderId).OnDelete(DeleteBehavior.Cascade);
            bid.HasIndex(b => new { b.AuctionId, b.AmountCents });
            bid.HasIndex(b => b.BidderId);
        });

        builder.Entity<Watch>(watch =>
        {
            watch.HasKey(w => new { w.UserId, w.TargetType, w.TargetId });
            watch.HasOne<ApplicationUser>().WithMany().HasForeignKey(w => w.UserId).OnDelete(DeleteBehavior.Cascade);
            watch.HasIndex(w => new { w.TargetType, w.TargetId });
        });
    }
}

/// <summary>The same model on PostgreSQL, with its own migrations in Data/Migrations/Postgres.</summary>
public sealed class PostgresDbContext(DbContextOptions<PostgresDbContext> options) : ApplicationDbContext(options);
