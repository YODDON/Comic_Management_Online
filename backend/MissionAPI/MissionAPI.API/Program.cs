using MissionAPI;
using MissionAPI.Data;
using MassTransit;
using MissionAPI.Consumers;
using SharedKernel.Extensions;

var builder = WebApplication.CreateBuilder(args);

DotNetEnv.Env.TraversePath().Load();
builder.Configuration.AddEnvironmentVariables();

// Controllers, DbContext, repositories, upload/Cloudinary, MediatR: see MissionModule.cs.
// Note: the module has no localhost connection fallback; the standalone API keeps it below.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MISSION_DB_CONNECTION"))
    && string.IsNullOrEmpty(builder.Configuration.GetConnectionString("MissionConnection")))
{
    builder.Configuration["ConnectionStrings:MissionConnection"] =
        "Server=localhost,1433;Database=MissionDB;User Id=sa;Password=Your_password123;TrustServerCertificate=True;";
}
builder.Services.AddMissionModule(builder.Configuration);
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(MissionAPI.Application.Features.Missions.Queries.GetAllMissionsQuery).Assembly));

builder.Services.AddCustomJwtAuthentication(builder.Configuration);

builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<MissionDbContext>(o =>
    {
        o.UseSqlServer();
        o.UseBusOutbox();
    });

    x.AddConsumer<MissionActivityRecordedConsumer>();

    x.UsingInMemory((context, cfg) =>
    {
        cfg.ReceiveEndpoint("mission-activity-recorded", e =>
        {
            e.ConfigureConsumer<MissionActivityRecordedConsumer>(context);
        });
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Dán access token JWT vào đây (không cần nhập tiền tố 'Bearer ')."
    });
    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

app.UseMissionModule();

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
