using CoreBanking.Domain.Accounts;
using CoreBanking.Domain.Customers;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Infrastructure;

public sealed class BankingDbContext(DbContextOptions<BankingDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Account> Accounts => Set<Account>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(entity =>
        {
            entity.HasKey(customer => customer.Id);
            entity.Property(customer => customer.Name).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(account => account.Id);
            entity.Property(account => account.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.HasOne<Customer>().WithMany().HasForeignKey(account => account.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
