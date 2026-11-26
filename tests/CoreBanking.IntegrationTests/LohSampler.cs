using System.Diagnostics;
using System.Globalization;

namespace CoreBanking.IntegrationTests;

internal sealed class LohSampler : IAsyncDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"corebanking-loh-{Guid.NewGuid():N}.csv");
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Process process;
    private string? error;

    public LohSampler(string executable, int processId, int durationSeconds)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "collect", "--process-id", processId.ToString(CultureInfo.InvariantCulture),
            "--refresh-interval", "1", "--counters", @"EventCounters\System.Runtime[loh-size]",
            "--format", "csv", "--output", path, "--duration", TimeSpan.FromSeconds(durationSeconds).ToString(@"hh\:mm\:ss") })
            start.ArgumentList.Add(argument);
        process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data?.Contains("Starting a counter session", StringComparison.Ordinal) == true)
                ready.TrySetResult();
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is { Length: > 0 } line) error = line.Length > 1024 ? line[..1024] : line;
        };
        if (!process.Start()) throw new InvalidOperationException("Não foi possível iniciar dotnet-counters.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public async Task WaitUntilReadyAsync()
    {
        var timeout = Stopwatch.StartNew();
        while (!ready.Task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (process.HasExited) throw new InvalidOperationException($"dotnet-counters encerrou: {error}");
            await Task.Delay(100);
        }
        if (!ready.Task.IsCompleted) throw new TimeoutException("dotnet-counters não iniciou a coleta.");
        await ready.Task;
    }

    public async Task<(double PeakBytes, int Samples)> FinishAsync()
    {
        if (process.HasExited)
            throw new InvalidOperationException("A coleta de LOH terminou antes do tráfego.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await process.WaitForExitAsync(timeout.Token);
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"dotnet-counters falhou: {error}");
        var maximum = 0d;
        var samples = 0;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var fields = line.Split(',');
            if (fields.Length != 5 || fields[2] != "LOH Size (B)" ||
                !double.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new InvalidOperationException("Formato inesperado na amostra de LOH.");
            maximum = Math.Max(maximum, value);
            samples++;
        }
        if (samples == 0) throw new InvalidOperationException("Nenhuma amostra de LOH foi recebida.");
        return (maximum, samples);
    }

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        process.Dispose();
        if (File.Exists(path)) File.Delete(path);
    }
}
