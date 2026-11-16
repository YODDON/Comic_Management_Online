using BannerAPI;
using ChapterAPI;
using ComicAPI.API;
using MassTransit;
using SharedKernel.Extensions;
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
builder.Services.AddUserModule(builder.Configuration);
builder.Services.AddWalletModule(builder.Configuration);

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
