using CoreBanking.Infrastructure;
using CoreBanking.Worker;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

var reprocessFailed = args.Length == 1 && args[0] == "--reprocess-failed";
var builder = Host.CreateApplicationBuilder(reprocessFailed ? [] : args);
builder.Logging.AddConsole(options => options.FormatterName = "json");
if (reprocessFailed)
{
    var uri = builder.Configuration["RabbitMQ:ConnectionString"]
        ?? throw new InvalidOperationException("Configure RabbitMQ:ConnectionString.");
    var factory = new ConnectionFactory { Uri = new Uri(uri) };
    await using var connection = await factory.CreateConnectionAsync();
    await using var channel = await connection.CreateChannelAsync(
        new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
    await TransferQueues.DeclareAsync(channel, CancellationToken.None);
    var reprocessed = await TransferQueues.ReprocessNextAsync(channel, CancellationToken.None);
    Console.WriteLine(reprocessed ? "Uma mensagem foi reenviada para processamento." : "A fila de falhas está vazia.");
    return;
}

builder.Services.AddDbContext<BankingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Banking")
        ?? throw new InvalidOperationException("Configure a conexão ConnectionStrings:Banking.")));
builder.Services.AddScoped<ITransferEventProcessor, TransferEventProcessor>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<PixWorker>();
builder.Services.AddHostedService<BacklogMonitor>();
builder.Services.AddHostedService<LedgerReconciliationMonitor>();

var host = builder.Build();
host.Run();
