namespace CoreBanking.Application;

public sealed record AuditActor
{
    private AuditActor(string kind, string? issuer, string? subject, string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128)
            throw new ArgumentException("Identificador de requisição inválido.", nameof(requestId));
        Kind = kind;
        Issuer = issuer;
        Subject = subject;
        RequestId = requestId;
    }

    public string Kind { get; }
    public string? Issuer { get; }
    public string? Subject { get; }
    public string RequestId { get; }

    public static AuditActor Local(string requestId) => new("LocalAnonymous", null, null, requestId);

    public static AuditActor Oidc(string issuer, string subject, string requestId)
    {
        if (string.IsNullOrWhiteSpace(issuer) || issuer.Length > 512)
            throw new ArgumentException("Emissor inválido.", nameof(issuer));
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256)
            throw new ArgumentException("Identificador do executor inválido.", nameof(subject));
        return new AuditActor("Oidc", issuer, subject, requestId);
    }
}
