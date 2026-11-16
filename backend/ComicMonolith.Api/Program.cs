using BannerAPI;
using SharedKernel.Extensions;
using UserAPI.API;

// Load .env once for the whole monolith (modules must not load it themselves).
DotNetEnv.Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddControllers();

// Modules
builder.Services.AddBannerModule(builder.Configuration);
builder.Services.AddUserModule(builder.Configuration);

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
app.UseUserModule();

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
