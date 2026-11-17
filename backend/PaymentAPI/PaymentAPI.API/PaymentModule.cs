using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentAPI.Application.Consumers;
using PaymentAPI.Application.Sagas;
using PaymentAPI.Controllers;
using PaymentAPI.Data;
using PaymentAPI.Domain.Entities;
using PaymentAPI.Interfaces;
using PaymentAPI.Repositories;
using PaymentAPI.Settings;

namespace PaymentAPI;

public static class PaymentModule
{
    public static IServiceCollection AddPaymentModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllers()
            .AddApplicationPart(typeof(PaymentsController).Assembly);

        var paymentConn = Environment.GetEnvironmentVariable("PAYMENT_DB_CONNECTION") ?? configuration.GetConnectionString("PaymentConnection");
        if (!string.IsNullOrEmpty(paymentConn))
        {
            services.AddDbContext<PaymentDbContext>(options => options.UseSqlServer(paymentConn));
        }

        services.AddScoped<IPaymentRepository, PaymentRepository>();

        // IPaymentChapterGateway and IPaymentWalletGateway are registered by AddChapterModule and
        // AddWalletModule (T17 group 3).

        services.Configure<TopUpSettings>(configuration.GetSection(TopUpSettings.SectionName));
        services.Configure<BankSettings>(configuration.GetSection(BankSettings.SectionName));

        // Not registered here: MassTransit. The host owns the single bus and calls AddPaymentConsumers /
        // ConfigurePaymentEndpoints below (T20). The host bus has no EF Outbox until T22 decides.

        return services;
    }

    /// <summary>
    /// Consumers and the PurchaseStateMachine saga of this module on the host's single bus (T20).
    /// Same as PaymentAPI's Program.cs (saga stored in PaymentDbContext), without the EF Outbox.
    /// </summary>
    public static IBusRegistrationConfigurator AddPaymentConsumers(this IBusRegistrationConfigurator bus)
    {
        bus.AddConsumer<PurchaseCompletedConsumer>();
        bus.AddConsumer<PurchaseFailedConsumer>();

        bus.AddSagaStateMachine<PurchaseStateMachine, PurchaseState>()
            .EntityFrameworkRepository(r =>
            {
                r.ExistingDbContext<PaymentDbContext>();
                r.UseSqlServer();
            });

        return bus;
    }

    /// <summary>Receive endpoints of this module (T20). Queue names unchanged from PaymentAPI's Program.cs.</summary>
    public static void ConfigurePaymentEndpoints(this IBusFactoryConfigurator cfg, IBusRegistrationContext context)
    {
        cfg.ReceiveEndpoint("payment-purchase-completed", e => e.ConfigureConsumer<PurchaseCompletedConsumer>(context));
        cfg.ReceiveEndpoint("payment-purchase-failed", e => e.ConfigureConsumer<PurchaseFailedConsumer>(context));
        cfg.ReceiveEndpoint("purchase-state", e => e.ConfigureSaga<PurchaseState>(context));
    }

    public static WebApplication UsePaymentModule(this WebApplication app)
    {
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetService<PaymentDbContext>();
            db?.Database.Migrate();
        }

        var topUp = app.Services.GetRequiredService<IOptions<TopUpSettings>>().Value;
        app.Logger.LogInformation("Top-up configured. CoinRate={CoinRate}", topUp.CoinRate);

        var bank = app.Services.GetRequiredService<IOptions<BankSettings>>().Value;
        if (!bank.IsWebhookConfigured)
        {
            // The webhook now rejects everything rather than trusting anyone, so deposits simply stop.
            app.Logger.LogWarning(
                "BankSettings__SepayApiKey is not set. The SePay webhook will reject every call and no deposit can be credited.");
        }

        if (!bank.IsBankConfigured)
        {
            app.Logger.LogWarning(
                "BankSettings__BankCode / BankSettings__AccountNumber are not set. Deposit QR generation is unavailable.");
        }
        // SepayApiKey is a secret and is never logged.

        return app;
    }
}
