using ChapterAPI;
using Microsoft.EntityFrameworkCore;
using ChapterAPI.Data;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SharedKernel.Extensions;
using System.Text;
using ChapterAPI.Interfaces;
using ChapterAPI.Repositories;
using ChapterAPI.Services;
using MissionAPI.Protos;
using MassTransit;
using ChapterAPI.Application.Consumers;

var builder = WebApplication.CreateBuilder(args);

DotNetEnv.Env.TraversePath().Load();
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddGrpc();

// Controllers, DbContext, Cloudinary, repositories, AutoMapper, MediatR: see ChapterModule.cs.
// Note: the module has no localhost connection fallback; the standalone API keeps it below.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHAPTER_DB_CONNECTION"))
    && string.IsNullOrEmpty(builder.Configuration.GetConnectionString("ChapterConnection")))
{
    builder.Configuration["ConnectionStrings:ChapterConnection"] =
        "Server=localhost;Database=ChapterDB;Trusted_Connection=True;TrustServerCertificate=True;";
}
builder.Services.AddChapterModule(builder.Configuration);

builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<ChapterAPI.Data.ChapterDbContext>(o =>
    {
        o.UseSqlServer();
        o.UseBusOutbox();
    });

    x.AddConsumer<UnlockChapterConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        var rabbitmqHost = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? builder.Configuration["RabbitMQ:Host"] ?? "localhost";
        cfg.Host(rabbitmqHost, "/", h => {
            h.Username("guest");
            h.Password("guest");
        });

        cfg.ReceiveEndpoint("unlock-chapter", e =>
        {
            e.ConfigureConsumer<UnlockChapterConsumer>(context);
        });
    });
});

builder.Services.AddCustomJwtAuthentication(builder.Configuration);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseChapterModule();

app.MapGrpcService<ChapterAPI.GrpcServices.ChapterGrpcService>();

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
