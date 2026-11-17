using ChapterAPI.Protos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentAPI.Controllers;
using PaymentAPI.Data;
using PaymentAPI.Interfaces;
using PaymentAPI.Repositories;
using PaymentAPI.Settings;
using WalletAPI.Protos;

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

        // Add MediatR for CQRS
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(PaymentAPI.Application.Features.Payments.Queries.GetBalanceQuery).Assembly));

        // Still gRPC (replaced in T16-T18). The host does not serve gRPC (ChapterGrpcService and
        // WalletGrpcService are not mapped), so these calls fail in the host until then.
        // See "Remaining gRPC calls" in PLAN.md.
        var chapterApiUrl = Environment.GetEnvironmentVariable("CHAPTER_API_URL") ?? configuration["GrpcEndpoints:ChapterAPI"] ?? "https://localhost:7147";
        // Explicit client name: the default name is the type name without namespace ("ChapterGrpcClient"),
        // which clashes with the ComicAPI and MissionAPI registrations once the modules share one host.
        services.AddGrpcClient<ChapterGrpc.ChapterGrpcClient>("PaymentAPI.ChapterGrpcClient", o =>
        {
            o.Address = new Uri(chapterApiUrl);
        });
        var walletApiUrl = Environment.GetEnvironmentVariable("WALLET_API_URL") ?? configuration["GrpcEndpoints:WalletAPI"] ?? "http://localhost:5091";
        services.AddGrpcClient<WalletService.WalletServiceClient>(o => o.Address = new Uri(walletApiUrl));

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
