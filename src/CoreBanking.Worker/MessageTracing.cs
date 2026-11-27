using System.Text;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CoreBanking.Worker;

internal static class MessageTracing
{
    public const string Header = "traceparent";

    public static string? ReadParent(IDictionary<string, object?>? headers, ILogger logger)
    {
        if (headers is null || !headers.TryGetValue(Header, out var value)) return null;
        var text = value switch
        {
            byte[] { Length: 55 } bytes => Encoding.ASCII.GetString(bytes),
            string { Length: 55 } item => item,
            _ => null
        };
        if (IsValid(text)) return text;
        logger.LogWarning("Cabeçalho de rastreamento inválido; a mensagem terá um novo rastreamento.");
        return null;
    }

    public static Dictionary<string, object?>? Headers(string? parent, int? attempt = null)
    {
        if (!IsValid(parent)) parent = null;
        if (parent is null && attempt is null) return null;
        var headers = new Dictionary<string, object?>();
        if (parent is not null) headers.Add(Header, parent);
        if (attempt.HasValue) headers.Add(TransferQueues.AttemptHeader, attempt.Value);
        return headers;
    }

    private static bool IsValid(string? parent) =>
        parent is { Length: 55 } && ActivityContext.TryParse(parent, null, true, out _);
}
