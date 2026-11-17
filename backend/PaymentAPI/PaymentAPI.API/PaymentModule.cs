using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentAPI.Controllers;
using PaymentAPI.Data;
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

        // Not registered here: MassTransit, the EF Outbox, PurchaseStateMachine (the saga) and
        // PurchaseCompletedConsumer / PurchaseFailedConsumer. The host owns the single bus, and these
        // do not run in the host until T20. Until then a chapter purchase started in the host is never
        // completed by the saga.

        return services;
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
