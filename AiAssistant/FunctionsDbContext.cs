namespace AiAssistant;
using Microsoft.EntityFrameworkCore;

public class FunctionsDbContext : DbContext
{
    private readonly String _connectionString;

    public FunctionsDbContext(String connectionString)
    {
        _connectionString = connectionString;
        _ = base.Database.EnsureCreated();
    }

    public DbSet<FunctionEntity> FunctionEntities { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseSqlite(_connectionString);
}
