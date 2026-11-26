using CoreBanking.Application;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Infrastructure;

internal static class OperationAuditWriter
{
    public static Task WriteAsync(BankingDbContext db, AuditActor actor, string action, string outcome,
        Guid operationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Uma transação ativa é necessária para registrar a auditoria.");
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "OperationAuditEvents"
                ("Id", "OccurredAt", "Action", "Outcome", "OperationId", "ActorKind", "Issuer", "Subject", "RequestId")
            VALUES
                ({Guid.NewGuid()}, clock_timestamp(), {action}, {outcome}, {operationId},
                 {actor.Kind}, {actor.Issuer}, {actor.Subject}, {actor.RequestId})
            """, cancellationToken);
    }
}
