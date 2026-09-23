using Maroos.Core.Scryfall;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Maroos.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<CachedCard> CachedCards => Set<CachedCard>();

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
    }
}
