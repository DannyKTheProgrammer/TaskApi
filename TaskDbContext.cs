using Microsoft.EntityFrameworkCore;

public class TaskDbContext(DbContextOptions<TaskDbContext> options) : DbContext(options)
{
    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskItem>(e =>
        {
            e.Property(x => x.Title).IsRequired().HasMaxLength(200);

            // В Npgsql версия строки типа uint маппится на системную колонку xmin
            e.Property(x => x.Version).IsRowVersion();
        });
    }
}
