namespace CoreBanking.Application;

public interface IAccountAccess
{
    Task<bool> HasAccessAsync(AuditActor actor, Guid accountId, CancellationToken cancellationToken);
    Task<bool> ChangeAsync(AuditActor actor, Guid accountId, string issuer, string subject, bool grant,
        CancellationToken cancellationToken);
}
