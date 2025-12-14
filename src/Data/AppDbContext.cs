using Microsoft.EntityFrameworkCore;

public class AppDbContext : DbContext
{
    public DbSet<ValidOrder> ValidOrders => Set<ValidOrder>();
    public DbSet<InvalidOrder> InvalidOrders => Set<InvalidOrder>();
    public DbSet<ProcessedFile> ProcessedFiles => Set<ProcessedFile>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite("Data Source=orders.db");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedFile>()
            .HasIndex(x => x.Hash)
            .IsUnique();
    }
}
