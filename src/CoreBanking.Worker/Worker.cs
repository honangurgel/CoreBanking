namespace CoreBanking.Worker;

public class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker host started without consumers configured.");
        return Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
