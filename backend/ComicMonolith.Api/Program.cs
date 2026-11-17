using BannerAPI;
using ChapterAPI;
using ComicAPI.API;
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

// Placeholder bus. T20 merges all module consumers into this single registration.
// In-memory, no consumers, no outbox, no RabbitMQ. Modules must not call AddMassTransit themselves.
builder.Services.AddMassTransit(x =>
{
    x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
});

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

app.UseRouting();
app.UseCors("Frontend");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();
