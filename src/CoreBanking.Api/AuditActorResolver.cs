using CoreBanking.Application;

namespace CoreBanking.Api;

internal static class AuditActorResolver
{
    public static AuditActor? Resolve(HttpContext context, bool authenticationDisabled)
    {
        if (authenticationDisabled) return AuditActor.Local(context.TraceIdentifier);
        if (context.User.Identity?.IsAuthenticated != true) return null;
        var subjects = context.User.FindAll("sub").Take(2).ToArray();
        var issuers = context.User.FindAll("iss").Take(2).ToArray();
        if (subjects.Length != 1 || issuers.Length != 1 ||
            string.IsNullOrWhiteSpace(subjects[0].Value) || subjects[0].Value.Length > 256 ||
            string.IsNullOrWhiteSpace(issuers[0].Value) || issuers[0].Value.Length > 512)
            return null;
        return AuditActor.Oidc(issuers[0].Value, subjects[0].Value, context.TraceIdentifier);
    }
}
