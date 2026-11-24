using CoreBanking.Infrastructure;
using CoreBanking.Worker;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDbContext<BankingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Banking")
        ?? throw new InvalidOperationException("Configure a conexão ConnectionStrings:Banking.")));
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
