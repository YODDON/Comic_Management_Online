using BannerAPI;
using ChapterAPI;
using ComicAPI.API;
using ComicMonolith.Api;
using MassTransit;
using MissionAPI;
using PaymentAPI;
using SharedKernel.Extensions;
using SocialAPI;
using UserAPI.API;
using WalletAPI;

// Load .env once for the whole monolith (modules must not load it themselves).
DotNetEnv.Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

// Fail fast before any module is registered (T12). Both run in every environment; the JWT check only
// throws outside Development (an unset ASPNETCORE_ENVIRONMENT counts as Production).
StartupChecks.ValidateConnectionStrings(builder.Configuration);
var jwtSecretWarning = StartupChecks.EnforceJwtSecret(builder.Configuration, builder.Environment);

builder.Services.AddControllers();

// Modules
builder.Services.AddBannerModule(builder.Configuration);
builder.Services.AddChapterModule(builder.Configuration);
builder.Services.AddComicModule(builder.Configuration);
builder.Services.AddMissionModule(builder.Configuration);
builder.Services.AddPaymentModule(builder.Configuration);
builder.Services.AddSocialModule(builder.Configuration);
builder.Services.AddUserModule(builder.Configuration);
builder.Services.AddWalletModule(builder.Configuration);

// MediatR and AutoMapper are registered once here for all modules (modules do not register them).
// A module whose Application assembly is missing from this list silently loses its handlers.
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblies(
    typeof(BannerAPI.Application.Features.Banners.Commands.CreateBannerCommand).Assembly,
    typeof(ChapterAPI.Application.Features.Chapters.Queries.GetChapterInfoQuery).Assembly,
    typeof(ComicAPI.Application.Features.Comics.Queries.GetComicsQuery).Assembly,
    typeof(MissionAPI.Application.Features.Missions.Queries.GetAllMissionsQuery).Assembly,
    typeof(PaymentAPI.Application.Features.Payments.Queries.GetBalanceQuery).Assembly,
    typeof(SocialAPI.Application.Features.Comments.Commands.CreateCommentCommand).Assembly,
    typeof(UserAPI.Application.DTOs.UserProfileDto).Assembly,
    typeof(WalletAPI.Application.Features.Currency.Queries.GetHistoryQuery).Assembly));
// Only Chapter and Comic have AutoMapper profiles; the other 6 modules do not use IMapper.
builder.Services.AddAutoMapper(
    typeof(ChapterAPI.Mappings.ChapterMappingProfile).Assembly,
    typeof(ComicAPI.Application.Mappings.ComicProfile).Assembly);

// The single bus for all modules (T20). Modules must not call AddMassTransit themselves; each one exposes
// AddXxxConsumers / ConfigureXxxEndpoints with the same consumers, saga and queue names as its old Program.cs.
// Still RabbitMQ (T21 switches to in-memory). No EF Outbox: one bus cannot carry the 5 modules' bus outboxes,
// and T22 decides whether it comes back. Banner, Social and User have no consumers.
builder.Services.AddMassTransit(x =>
{
    x.AddChapterConsumers();
    x.AddComicConsumers();
    x.AddMissionConsumers();
    x.AddPaymentConsumers();
    x.AddWalletConsumers();

    x.UsingRabbitMq((context, cfg) =>
    {
        var rabbitmqHost = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? builder.Configuration["RabbitMQ:Host"] ?? "localhost";
        cfg.Host(rabbitmqHost, "/", h =>
        {
            h.Username("guest");
            h.Password("guest");
        });

        cfg.ConfigureChapterEndpoints(context);
        cfg.ConfigureComicEndpoints(context);
        cfg.ConfigureMissionEndpoints(context);
        cfg.ConfigurePaymentEndpoints(context);
        cfg.ConfigureWalletEndpoints(context);
    });
});
// Host start waits until the bus and its queue bindings are ready, so the first messages after start are not
// dropped as unroutable (found by T39). Start blocks while RabbitMQ is unreachable.
builder.Services.Configure<MassTransitHostOptions>(o => o.WaitUntilStarted = true);

builder.Services.AddCustomJwtAuthentication(builder.Configuration);
builder.Services.AddAuthorization();

var frontendOrigin = builder.Configuration["FRONTEND_URL"] ?? "http://localhost:5173";
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins(frontendOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (jwtSecretWarning is not null)
{
    app.Logger.LogWarning("{Message}", jwtSecretWarning);
}

app.UseBannerModule();
app.UseChapterModule();
app.UseComicModule();
app.UseMissionModule();
app.UsePaymentModule();
app.UseSocialModule();
app.UseUserModule();
app.UseWalletModule();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Replaces the YARP gateway's path transforms (/comics -> /api/comics ...). Must run before UseRouting.
app.UsePathRewrite();

// The gateway's own JSON 404 for a path that matched no route (controller 404s are left alone).
app.UseNoRouteJson404();

app.UseRouting();
app.UseCors("Frontend");

app.UseAuthentication();
app.UseAuthorization();

// The gateway's public endpoint list (T14). Needs the endpoint metadata, so it runs after routing and auth.
app.UsePublicEndpointGate();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();
