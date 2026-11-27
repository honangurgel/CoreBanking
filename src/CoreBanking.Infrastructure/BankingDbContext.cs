using CoreBanking.Domain.Accounts;
using CoreBanking.Domain.Customers;
using CoreBanking.Domain.Ledger;
using CoreBanking.Infrastructure.Deposits;
using CoreBanking.Infrastructure.Messaging;
using CoreBanking.Infrastructure.Pix;
using CoreBanking.Infrastructure.Transfers;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Infrastructure;

public sealed class BankingDbContext(DbContextOptions<BankingDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
    public DbSet<TransferRecord> Transfers => Set<TransferRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<DepositRecord> Deposits => Set<DepositRecord>();
    public DbSet<PixKey> PixKeys => Set<PixKey>();
    public DbSet<PixOperation> PixOperations => Set<PixOperation>();
    public DbSet<PixTransition> PixTransitions => Set<PixTransition>();

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

        modelBuilder.Entity<LedgerAccount>(entity =>
        {
            entity.ToTable("LedgerAccounts", table => table.HasCheckConstraint(
                "CK_LedgerAccounts_CustomerBalance", "\"CustomerAccountId\" IS NULL OR \"BalanceMinor\" >= 0"));
            entity.HasKey(account => account.Id);
            entity.HasIndex(account => account.CustomerAccountId).IsUnique();
            entity.HasOne<Account>().WithOne().HasForeignKey<LedgerAccount>(account => account.CustomerAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PixKey>(entity =>
        {
            entity.HasKey(key => key.Id);
            entity.HasOne<Account>().WithMany().HasForeignKey(key => key.AccountId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<PixOperation>(entity =>
        {
            entity.HasKey(operation => operation.Id);
            entity.HasIndex(operation => new { operation.AccountId, operation.IdempotencyKey }).IsUnique();
            entity.HasIndex(operation => new { operation.Status, operation.NextAttemptAt });
            entity.Property(operation => operation.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(operation => operation.InstitutionIspb).HasMaxLength(8).IsRequired();
            entity.Property(operation => operation.Direction).HasMaxLength(8).IsRequired();
            entity.Property(operation => operation.Scenario).HasMaxLength(8).IsRequired();
            entity.Property(operation => operation.Status).HasMaxLength(32).IsRequired();
            entity.Property(operation => operation.TraceParent).HasMaxLength(55);
            entity.HasOne<Account>().WithMany().HasForeignKey(operation => operation.AccountId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable("PixOperations", table =>
            {
                table.HasCheckConstraint("CK_PixOperations_Amount", "\"AmountMinor\" > 0 AND \"AmountMinor\" <= 99999999999999999");
                table.HasCheckConstraint("CK_PixOperations_Direction", "\"Direction\" IN ('Outgoing', 'Incoming')");
                table.HasCheckConstraint("CK_PixOperations_Scenario", "\"Scenario\" IN ('Confirm', 'Reject')");
                table.HasCheckConstraint("CK_PixOperations_Status", "\"Status\" IN ('Pending', 'Confirmed', 'Rejected', 'RequiresReview', 'InsufficientFunds')");
                table.HasCheckConstraint("CK_PixOperations_Attempts", "\"Attempts\" BETWEEN 0 AND 3");
            });
        });
        modelBuilder.Entity<PixTransition>(entity =>
        {
            entity.HasKey(transition => transition.Id);
            entity.Property(transition => transition.Status).HasMaxLength(32).IsRequired();
            entity.HasOne<PixOperation>().WithMany().HasForeignKey(transition => transition.OperationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TransferRecord>(entity =>
        {
            entity.HasKey(transfer => transfer.Id);
            entity.HasIndex(transfer => new { transfer.SourceAccountId, transfer.DestinationAccountId, transfer.IdempotencyKey })
                .HasDatabaseName("IX_Transfers_AccountPair_IdempotencyKey").IsUnique();
            entity.Property(transfer => transfer.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(transfer => transfer.Status).HasMaxLength(32).IsRequired();
            entity.ToTable("Transfers", table =>
            {
                table.HasCheckConstraint("CK_Transfers_Amount", "\"AmountMinor\" > 0");
                table.HasCheckConstraint("CK_Transfers_Accounts", "\"SourceAccountId\" <> \"DestinationAccountId\"");
                table.HasCheckConstraint("CK_Transfers_Status", "\"Status\" IN ('Pending', 'Completed', 'InsufficientFunds')");
            });
            modelBuilder.Entity<OutboxMessage>(entity =>
            {
                entity.HasKey(message => message.Id);
                entity.HasIndex(message => message.TransferId).IsUnique();
                entity.Property(message => message.TraceParent).HasMaxLength(55);
                entity.HasOne<TransferRecord>().WithMany().HasForeignKey(message => message.TransferId)
                    .OnDelete(DeleteBehavior.Restrict);
            });
            modelBuilder.Entity<InboxMessage>(entity =>
            {
                entity.HasKey(message => message.Id);
                entity.HasOne<TransferRecord>().WithMany().HasForeignKey(message => message.TransferId)
                    .OnDelete(DeleteBehavior.Restrict);
            });
            modelBuilder.Entity<DepositRecord>(entity =>
            {
                entity.HasKey(deposit => deposit.Id);
                entity.HasIndex(deposit => new { deposit.AccountId, deposit.IdempotencyKey }).IsUnique();
                entity.Property(deposit => deposit.IdempotencyKey).HasMaxLength(128).IsRequired();
                entity.ToTable("Deposits", table =>
                    table.HasCheckConstraint("CK_Deposits_Amount", "\"AmountMinor\" > 0"));
            });
        });
    }
}
