using WalletAPI;
using WalletAPI.Data;
using SharedKernel.Extensions;
using MassTransit;
using WalletAPI.Application.Consumers;

var builder = WebApplication.CreateBuilder(args);

DotNetEnv.Env.TraversePath().Load();
builder.Configuration.AddEnvironmentVariables();

// Neither the standalone API nor the module has a hardcoded connection string (no default password).
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WALLET_DB_CONNECTION"))
    && string.IsNullOrEmpty(builder.Configuration.GetConnectionString("WalletConnection"))
    && string.IsNullOrEmpty(builder.Configuration.GetConnectionString("DefaultConnection")))
{
    throw new InvalidOperationException(
        "WALLET_DB_CONNECTION (or ConnectionStrings:WalletConnection / DefaultConnection) is not set. See backend/.env.example.");
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

    x.UsingInMemory((context, cfg) =>
    {
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

app.Run();
