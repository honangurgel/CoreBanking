using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CoreBanking.Infrastructure;

public sealed class BankingDbContextFactory : IDesignTimeDbContextFactory<BankingDbContext>
{
    public BankingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Banking")
            ?? throw new InvalidOperationException("Defina ConnectionStrings__Banking para usar as ferramentas de migração.");
        var options = new DbContextOptionsBuilder<BankingDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new BankingDbContext(options);
    }
}
