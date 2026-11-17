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

        // In-process replacement for the gRPC call PaymentAPI -> WalletAPI (T17).
        services.AddScoped<SharedKernel.Contracts.Modules.IPaymentWalletGateway, WalletAPI.ModuleServices.PaymentWalletGateway>();

        var walletConn = Environment.GetEnvironmentVariable("WALLET_DB_CONNECTION")
            ?? configuration.GetConnectionString("WalletConnection")
            ?? configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrEmpty(walletConn))
        {
            services.AddDbContext<WalletDbContext>(options => options.UseSqlServer(walletConn));
        }

        // Not registered here (no bus in the host until T20): the MassTransit consumers
        // (MissionRewardGranted, DebitWallet, RefundWallet) and the EF Outbox on WalletDbContext.

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
