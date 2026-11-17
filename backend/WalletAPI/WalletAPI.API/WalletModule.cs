using Microsoft.EntityFrameworkCore;
using WalletAPI.Controllers;
using WalletAPI.Data;
using WalletAPI.Interfaces;
using WalletAPI.Repositories;

namespace WalletAPI;

public static class WalletModule
{
    public static IServiceCollection AddWalletModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllers()
            .AddApplicationPart(typeof(CurrencyController).Assembly)
            .AddJsonOptions(options =>
                options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

        services.AddScoped<ICurrencyRepository, CurrencyRepository>();
        services.AddScoped<IWithdrawRepository, WithdrawRepository>();
        services.AddScoped<IWalletRepository, WalletRepository>();

        var walletConn = Environment.GetEnvironmentVariable("WALLET_DB_CONNECTION")
            ?? configuration.GetConnectionString("WalletConnection")
            ?? configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrEmpty(walletConn))
        {
            services.AddDbContext<WalletDbContext>(options => options.UseSqlServer(walletConn));
        }

        // Not registered here (no bus in the host until T20): the MassTransit consumers
        // (MissionRewardGranted, DebitWallet, RefundWallet) and the EF Outbox on WalletDbContext.
        // Also not mapped: WalletGrpcService (gRPC is replaced in T16-T18).

        return services;
    }

    public static WebApplication UseWalletModule(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetService<WalletDbContext>();
        db?.Database.Migrate();

        return app;
    }
}
