using MassTransit;
using Microsoft.EntityFrameworkCore;
using WalletAPI.Application.Consumers;
using WalletAPI.Consumers;
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

        // Not registered here: MassTransit. The host owns the single bus and calls AddWalletConsumers /
        // ConfigureWalletEndpoints below (T20). The host bus has no EF Outbox until T22 decides; the
        // Serializable isolation of debits and credits is in WalletRepository, not in the outbox.

        return services;
    }

    /// <summary>Consumers of this module on the host's single bus (T20). Same as WalletAPI's Program.cs, without the EF Outbox.</summary>
    public static IBusRegistrationConfigurator AddWalletConsumers(this IBusRegistrationConfigurator bus)
    {
        bus.AddConsumer<MissionRewardGrantedConsumer>();
        bus.AddConsumer<DebitWalletConsumer>();
        bus.AddConsumer<RefundWalletConsumer>();
        return bus;
    }

    /// <summary>Receive endpoints of this module (T20). Queue names unchanged from WalletAPI's Program.cs.</summary>
    public static void ConfigureWalletEndpoints(this IBusFactoryConfigurator cfg, IBusRegistrationContext context)
    {
        cfg.ReceiveEndpoint("mission-reward-granted", e => e.ConfigureConsumer<MissionRewardGrantedConsumer>(context));
        cfg.ReceiveEndpoint("debit-wallet", e => e.ConfigureConsumer<DebitWalletConsumer>(context));
        cfg.ReceiveEndpoint("refund-wallet", e => e.ConfigureConsumer<RefundWalletConsumer>(context));
    }

    public static WebApplication UseWalletModule(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetService<WalletDbContext>();
        db?.Database.Migrate();

        return app;
    }
}
