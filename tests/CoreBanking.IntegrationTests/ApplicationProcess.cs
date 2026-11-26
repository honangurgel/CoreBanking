using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CoreBanking.IntegrationTests;

internal sealed class ApplicationProcess : IAsyncDisposable
{
    private static readonly Regex AddressPattern = new(@"http://127\.0\.0\.1:\d+", RegexOptions.CultureInvariant);
    private readonly TaskCompletionSource<Uri> address = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> errors = new();
    private readonly Process process;
    public int Id => process.Id;

    public ApplicationProcess(string project, IReadOnlyDictionary<string, string> environment, string? url = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Parent?.Name
            ?? throw new InvalidOperationException("Não foi possível determinar a configuração do build.");
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CoreBanking.sln")))
            directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Raiz da solução não encontrada.");
        var output = Path.Combine(directory.FullName, "src", project, "bin", configuration, "net10.0");
        start.WorkingDirectory = output;
        start.ArgumentList.Add(Path.Combine(output, $"{project}.dll"));
        foreach (var setting in environment) start.Environment[setting.Key] = setting.Value;
        if (url is not null) start.Environment["ASPNETCORE_URLS"] = url;
        process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not { } line) return;
            if (line.Contains("\"LogLevel\":\"Error\"") || line.Contains("\"LogLevel\":\"Critical\""))
                CaptureError(line);
            if (line.Contains("Now listening on:"))
            {
                var match = AddressPattern.Match(line);
                if (match.Success) address.TrySetResult(new Uri(match.Value));
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is { } line) CaptureError(line);
        };
        if (!process.Start()) throw new InvalidOperationException($"Não foi possível iniciar {project}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public async Task<Uri> WaitForAddressAsync()
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            ThrowIfExited();
            if (address.Task.IsCompleted) return await address.Task;
            await Task.Delay(100);
        }
        throw new TimeoutException("O processo não informou endereço HTTP em 60 segundos.");
    }

    public void ThrowIfExited()
    {
        if (process.HasExited)
            throw new InvalidOperationException($"Processo {process.Id} terminou com código {process.ExitCode}: {string.Join(" | ", errors)}");
    }

    private void CaptureError(string line)
    {
        errors.Enqueue(line.Length > 1024 ? line[..1024] : line);
        while (errors.Count > 10) errors.TryDequeue(out _);
    }

    public async Task StopAsync()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        process.WaitForExit();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        process.Dispose();
    }
}
