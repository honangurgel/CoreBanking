using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CoreBanking.IntegrationTests;

internal static class TestServerLoopback
{
    public static void UseTestLoopback(this IWebHostBuilder builder) =>
        builder.UseTestRemoteAddress(IPAddress.Loopback);

    public static void UseTestRemoteAddress(this IWebHostBuilder builder, IPAddress address) =>
        builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new RemoteAddressFilter(address)));

    private sealed class RemoteAddressFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                await nextMiddleware();
            });
            next(app);
        };
    }
}
