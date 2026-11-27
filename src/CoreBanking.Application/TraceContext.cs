using System.Diagnostics;

namespace CoreBanking.Application;

public static class TraceContext
{
    public static string? Capture()
    {
        var activity = Activity.Current;
        return activity is { IdFormat: ActivityIdFormat.W3C, Recorded: true } && activity.Id is { Length: 55 } id
            ? id : null;
    }
}
