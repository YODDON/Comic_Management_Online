using WalletAPI;
using WalletAPI.Data;
using SharedKernel.Extensions;
using MassTransit;
using WalletAPI.Application.Consumers;

var builder = WebApplication.CreateBuilder(args);

DotNetEnv.Env.TraversePath().Load();
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddGrpc();

// Standalone API keeps its old localhost fallback; the module itself has no hardcoded default.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WALLET_DB_CONNECTION"))
    && string.IsNullOrEmpty(builder.Configuration.GetConnectionString("WalletConnection"))
    && string.IsNullOrEmpty(builder.Configuration.GetConnectionString("DefaultConnection")))
{
    builder.Configuration["ConnectionStrings:WalletConnection"] = "Server=localhost,1433;Database=WalletDB;User Id=sa;Password=Your_password123;TrustServerCertificate=True;";
}

builder.Services.AddWalletModule(builder.Configuration);
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(WalletAPI.Application.Features.Currency.Queries.GetHistoryQuery).Assembly));

builder.Services.AddCustomJwtAuthentication(builder.Configuration);

builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<WalletDbContext>(o =>
    {
        o.UseSqlServer();
        o.UseBusOutbox();
        o.IsolationLevel = System.Data.IsolationLevel.Serializable;
    });

    x.AddConsumer<WalletAPI.Consumers.MissionRewardGrantedConsumer>();
    x.AddConsumer<DebitWalletConsumer>();
    x.AddConsumer<RefundWalletConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        var rabbitmqHost = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? builder.Configuration["RabbitMQ:Host"] ?? "localhost";
        cfg.Host(rabbitmqHost, "/", h => {
            h.Username("guest");
            h.Password("guest");
        });

        cfg.ReceiveEndpoint("mission-reward-granted", e =>
        {
            e.ConfigureConsumer<WalletAPI.Consumers.MissionRewardGrantedConsumer>(context);
        });

        cfg.ReceiveEndpoint("debit-wallet", e =>
        {
            e.ConfigureConsumer<DebitWalletConsumer>(context);
        });

        cfg.ReceiveEndpoint("refund-wallet", e =>
        {
            e.ConfigureConsumer<RefundWalletConsumer>(context);
        });
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseWalletModule();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// HTTPS is terminated at ApiGateway; internal service traffic stays on HTTP.

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGrpcService<WalletAPI.GrpcServices.WalletGrpcService>();

app.Run();
