using SocialAPI;
using SharedKernel.Extensions;
using MassTransit;

var builder = WebApplication.CreateBuilder(args);

DotNetEnv.Env.TraversePath().Load();
builder.Configuration.AddEnvironmentVariables();

// Controllers, DbContext, ComicValidator, repositories, MediatR: see SocialModule.cs.
// Note: the module has no localhost connection fallback; the standalone API keeps it below.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SOCIAL_DB_CONNECTION"))
    && string.IsNullOrEmpty(builder.Configuration.GetConnectionString("SocialConnection")))
{
    builder.Configuration["ConnectionStrings:SocialConnection"] =
        "Server=localhost;Database=SocialDB;Trusted_Connection=True;TrustServerCertificate=True;";
}
builder.Services.AddSocialModule(builder.Configuration);
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(SocialAPI.Application.Features.Comments.Commands.CreateCommentCommand).Assembly));

builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<SocialAPI.Data.SocialDbContext>(o =>
    {
        o.UseSqlServer();
        o.UseBusOutbox();
    });

    x.UsingRabbitMq((context, cfg) =>
    {
        var rabbitmqHost = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? builder.Configuration["RabbitMQ:Host"] ?? "localhost";
        cfg.Host(rabbitmqHost, "/", h => {
            h.Username("guest");
            h.Password("guest");
        });
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCustomJwtAuthentication(builder.Configuration);

var app = builder.Build();

app.UseSocialModule();

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
