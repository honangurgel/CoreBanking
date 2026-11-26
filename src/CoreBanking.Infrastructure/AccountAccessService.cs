using CoreBanking.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreBanking.Infrastructure;

public sealed class AccountAccessService(BankingDbContext db) : IAccountAccess, IAsyncDisposable
{
    private NpgsqlConnection? accessConnection;
    private NpgsqlTransaction? accessTransaction;

    public async Task<bool> HasAccessAsync(AuditActor actor, Guid accountId, CancellationToken cancellationToken)
    {
        if (actor.Kind != "Oidc") return false;
        if (accessConnection is null)
        {
            var settings = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
            settings.ApplicationName += "|CoreBanking.AccountAccess";
            accessConnection = new NpgsqlConnection(settings.ConnectionString);
            await accessConnection.OpenAsync(cancellationToken);
            accessTransaction = await accessConnection.BeginTransactionAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand("""
            SELECT 1 FROM "AccountAccessGrants"
            WHERE "AccountId" = @account AND "Issuer" = @issuer AND "Subject" = @subject
            FOR SHARE
            """, accessConnection, accessTransaction);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("issuer", actor.Issuer!);
        command.Parameters.AddWithValue("subject", actor.Subject!);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (accessTransaction is not null) await accessTransaction.DisposeAsync();
        }
        finally
        {
            if (accessConnection is not null) await accessConnection.DisposeAsync();
            accessTransaction = null;
            accessConnection = null;
        }
    }

    public async Task<bool> ChangeAsync(AuditActor actor, Guid accountId, string issuer, string subject,
        bool grant, CancellationToken cancellationToken)
    {
        if (actor.Kind != "Oidc") throw new InvalidOperationException("Gestão de acesso exige identidade OIDC.");
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.Accounts.AsNoTracking().AnyAsync(item => item.Id == accountId, cancellationToken)) return false;
        await ChangeWithinTransactionAsync(db, actor, accountId, issuer, subject, grant, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    internal static async Task ChangeWithinTransactionAsync(BankingDbContext db, AuditActor actor, Guid accountId,
        string issuer, string subject, bool grant, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null || actor.Kind != "Oidc")
            throw new InvalidOperationException("Identidade OIDC e transação são necessárias.");
        var affected = grant
            ? await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "AccountAccessGrants" ("AccountId", "Issuer", "Subject")
                VALUES ({accountId}, {issuer}, {subject}) ON CONFLICT DO NOTHING
                """, cancellationToken)
            : await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM "AccountAccessGrants"
                WHERE "AccountId" = {accountId} AND "Issuer" = {issuer} AND "Subject" = {subject}
                """, cancellationToken);
        if (affected == 0) return;
        var action = grant ? "Grant" : "Revoke";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AccountAccessAudit"
                ("Id", "OccurredAt", "AccountId", "Action", "TargetIssuer", "TargetSubject", "ActorIssuer", "ActorSubject", "RequestId")
            VALUES ({Guid.NewGuid()}, clock_timestamp(), {accountId}, {action}, {issuer}, {subject},
                {actor.Issuer}, {actor.Subject}, {actor.RequestId})
            """, cancellationToken);
    }
}
