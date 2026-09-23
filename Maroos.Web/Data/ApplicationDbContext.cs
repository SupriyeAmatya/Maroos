using Maroos.Core.Decks;
using Maroos.Core.Scryfall;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Maroos.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<CachedCard> CachedCards => Set<CachedCard>();
    public DbSet<Deck> Decks => Set<Deck>();
    public DbSet<DeckCard> DeckCards => Set<DeckCard>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

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
    }
}
