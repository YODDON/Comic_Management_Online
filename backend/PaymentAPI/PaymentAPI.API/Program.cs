using PaymentAPI;
using System.Text;
using SharedKernel.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PaymentAPI.Data;
using PaymentAPI.Interfaces;
using PaymentAPI.Repositories;
using PaymentAPI.Services;
using ChapterAPI.Protos;
using WalletAPI.Protos;
using Microsoft.Extensions.Options;
using PaymentAPI.Settings;
using MassTransit;
using PaymentAPI.Application.Sagas;
using PaymentAPI.Domain.Entities;
using PaymentAPI.Application.Consumers;

var builder = WebApplication.CreateBuilder(args);

DotNetEnv.Env.TraversePath().Load();
builder.Configuration.AddEnvironmentVariables();

// Controllers, DbContext, repository, MediatR, gRPC clients, TopUp/Bank settings: see PaymentModule.cs.
builder.Services.AddPaymentModule(builder.Configuration);
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(PaymentAPI.Application.Features.Payments.Queries.GetBalanceQuery).Assembly));

builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<PaymentDbContext>(o =>
    {
        o.UseSqlServer();
        o.UseBusOutbox();
    });

    x.AddConsumer<PurchaseCompletedConsumer>();
    x.AddConsumer<PurchaseFailedConsumer>();

    x.AddSagaStateMachine<PurchaseStateMachine, PurchaseState>()
        .EntityFrameworkRepository(r =>
        {
            r.ExistingDbContext<PaymentDbContext>();
            r.UseSqlServer();
        });

    x.UsingRabbitMq((context, cfg) =>
    {
        var rabbitmqHost = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? builder.Configuration["RabbitMQ:Host"] ?? "localhost";
        cfg.Host(rabbitmqHost, "/", h => {
            h.Username("guest");
            h.Password("guest");
        });

        cfg.ReceiveEndpoint("payment-purchase-completed", e =>
        {
            e.ConfigureConsumer<PurchaseCompletedConsumer>(context);
        });
        
        cfg.ReceiveEndpoint("payment-purchase-failed", e =>
        {
            e.ConfigureConsumer<PurchaseFailedConsumer>(context);
        });

        cfg.ReceiveEndpoint("purchase-state", e =>
        {
            e.ConfigureSaga<PurchaseState>(context);
        });
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCustomJwtAuthentication(builder.Configuration);

var app = builder.Build();

// Migrate() and the top-up / bank startup log lines: see PaymentModule.cs.
app.UsePaymentModule();

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
