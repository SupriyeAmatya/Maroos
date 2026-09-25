// =====================================================================================================
// FILE: Maroos.Web/Data/ApplicationDbContext.cs
// PURPOSE: The EF Core "DbContext" — the app's view of the database. Each DbSet<T> property is a table;
// OnModelCreating describes keys, column sizes, relationships (foreign keys) and indexes.
//
// TABLES (besides Identity's AspNetUsers / AspNetRoles / ... tables inherited from IdentityDbContext):
//   CachedCards        — Scryfall card JSON cached for 24 h           (Maroos.Core/Scryfall/CachedCard.cs)
//   Decks, DeckCards   — decks and the cards in them                  (Maroos.Core/Decks/Deck.cs)
//   Auctions, Bids     — listings and accepted bids                   (Maroos.Core/Auctions)
//   Watches            — watchlist entries                            (Maroos.Core/Watchlist/Watch.cs)
//   DataProtectionKeys — the keys that sign login cookies
//   OutgoingEmails     — the email outbox                          (Maroos.Core/Email/OutgoingEmail.cs)
//
// Every change to this model needs a migration for BOTH databases (see DEVELOPMENT.md); a test
// (MigrationsAreCompleteTests) fails if one is missing.
// =====================================================================================================

using Maroos.Core.Auctions;
using Maroos.Core.Decks;
using Maroos.Core.Email;
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
// IdentityDbContext<ApplicationUser>: adds all of Identity's tables (users, logins, roles, tokens, passkeys).
// IDataProtectionKeyContext: lets Data Protection store its keys here (PersistKeysToDbContext in Program.cs).
public class ApplicationDbContext : IdentityDbContext<ApplicationUser>, IDataProtectionKeyContext
{
    // Constructor used on SQLite; `options` carries the provider and connection string from DatabaseSetup.
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }

    /// <summary>For provider-specific subclasses such as <see cref="PostgresDbContext"/>.</summary>
    protected ApplicationDbContext(DbContextOptions options) : base(options)
    {
    }

    // The tables. `=> Set<T>()` returns EF's table object each time (no field needed).
    public DbSet<CachedCard> CachedCards => Set<CachedCard>();
    public DbSet<Deck> Decks => Set<Deck>();
    public DbSet<DeckCard> DeckCards => Set<DeckCard>();
    public DbSet<Auction> Auctions => Set<Auction>();
    public DbSet<Bid> Bids => Set<Bid>();
    public DbSet<Watch> Watches => Set<Watch>();
    // OutgoingEmails: queued, sent and given-up emails (see Services/Email).
    public DbSet<OutgoingEmail> OutgoingEmails => Set<OutgoingEmail>();

    /// <summary>ASP.NET Core Data Protection keys (they sign login cookies), so they survive redeploys.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    // OnModelCreating: EF calls this once to learn the shape of the database ("fluent API").
    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Let Identity configure its own tables first.
        base.OnModelCreating(builder);

        // Users: limit the display name column to 40 characters.
        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(ApplicationUser.MaxDisplayNameLength);
        });

        // Cached cards: keyed by Scryfall's id (we never generate it), with an index for lookups by name.
        builder.Entity<CachedCard>(card =>
        {
            card.HasKey(c => c.ScryfallId);
            card.Property(c => c.ScryfallId).ValueGeneratedNever();
            card.Property(c => c.Name).HasMaxLength(200);
            card.HasIndex(c => c.Name);
        });

        // Decks: each belongs to a user (deleting the user deletes their decks) and has many cards
        // (deleting a deck deletes its cards). The index makes "my decks, newest first" fast.
        builder.Entity<Deck>(deck =>
        {
            deck.HasKey(d => d.Id);
            deck.Property(d => d.Name).HasMaxLength(Deck.MaxNameLength);
            deck.Property(d => d.Format).HasMaxLength(40);
            deck.HasOne<ApplicationUser>().WithMany().HasForeignKey(d => d.OwnerId).OnDelete(DeleteBehavior.Cascade);
            deck.HasMany(d => d.Cards).WithOne().HasForeignKey(c => c.DeckId).OnDelete(DeleteBehavior.Cascade);
            deck.HasIndex(d => new { d.OwnerId, d.UpdatedAt });
            // A copy keeps pointing at its original until the original is deleted; then the copy just loses its credit.
            deck.HasOne<Deck>().WithMany().HasForeignKey(d => d.CopiedFromDeckId).OnDelete(DeleteBehavior.SetNull);
        });

        // Deck cards: a "composite key" — one row per (deck, printing, board). The same card in the main
        // board and the sideboard is two rows; adding it again to the same board just raises Quantity.
        builder.Entity<DeckCard>(card =>
        {
            card.HasKey(c => new { c.DeckId, c.ScryfallId, c.Board });
            card.Property(c => c.CardName).HasMaxLength(200);
            card.Property(c => c.SetCode).HasMaxLength(10);
            card.Property(c => c.CollectorNumber).HasMaxLength(20);
        });

        // Auctions: the seller is required (deleting the seller deletes their listings); the winner is optional
        // (deleting the winner keeps the auction and clears WinnerId). Indexes serve "open auctions ending soon"
        // and "my listings".
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

        // Bids: belong to an auction and a bidder (both cascade). (AuctionId, AmountCents) finds the high bid quickly;
        // BidderId serves the "my bids" profile tab.
        builder.Entity<Bid>(bid =>
        {
            bid.HasKey(b => b.Id);
            bid.HasOne<Auction>().WithMany().HasForeignKey(b => b.AuctionId).OnDelete(DeleteBehavior.Cascade);
            bid.HasOne<ApplicationUser>().WithMany().HasForeignKey(b => b.BidderId).OnDelete(DeleteBehavior.Cascade);
            bid.HasIndex(b => new { b.AuctionId, b.AmountCents });
            bid.HasIndex(b => b.BidderId);
        });

        // Watches: one row per (user, kind, target) — so you can't watch the same thing twice.
        // The (TargetType, TargetId) index finds everyone watching a deck when it's deleted.
        builder.Entity<Watch>(watch =>
        {
            watch.HasKey(w => new { w.UserId, w.TargetType, w.TargetId });
            watch.HasOne<ApplicationUser>().WithMany().HasForeignKey(w => w.UserId).OnDelete(DeleteBehavior.Cascade);
            watch.HasIndex(w => new { w.TargetType, w.TargetId });
        });

        // Outgoing emails: the recipient's account is optional (deleting a user deletes their queued emails).
        // The (SentAtUtc, NextAttemptAtUtc) index lets the dispatcher find due emails quickly.
        builder.Entity<OutgoingEmail>(email =>
        {
            email.HasKey(e => e.Id);
            email.Property(e => e.ToAddress).HasMaxLength(OutgoingEmail.MaxAddressLength);
            email.Property(e => e.Subject).HasMaxLength(OutgoingEmail.MaxSubjectLength);
            email.Property(e => e.LastError).HasMaxLength(OutgoingEmail.MaxErrorLength);
            email.HasOne<ApplicationUser>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
            email.HasIndex(e => new { e.SentAtUtc, e.NextAttemptAtUtc });
        });
    }
}

/// <summary>The same model on PostgreSQL, with its own migrations in Data/Migrations/Postgres.</summary>
// PostgresDbContext adds nothing — it exists only so EF keeps a separate migration history for PostgreSQL.
public sealed class PostgresDbContext(DbContextOptions<PostgresDbContext> options) : ApplicationDbContext(options);
