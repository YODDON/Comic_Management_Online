using Microsoft.EntityFrameworkCore;
using UserAPI.API.Controllers;
using UserAPI.Application.Interfaces;
using UserAPI.Application.Services;
using UserAPI.Domain.Interfaces;
using UserAPI.Infrastructure.Data;
using UserAPI.Infrastructure.Repositories;
using UserAPI.Infrastructure.Services;

namespace UserAPI.API;

public static class UserModule
{
    public static IServiceCollection AddUserModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllers()
            .AddApplicationPart(typeof(AuthController).Assembly);

        var userConn = Environment.GetEnvironmentVariable("AUTH_DB_CONNECTION")
            ?? configuration.GetConnectionString("AuthConnection");
        if (!string.IsNullOrEmpty(userConn))
        {
            services.AddDbContext<UserDbContext>(options => options.UseSqlServer(userConn));
        }

        services.AddMemoryCache();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IAdminUserRepository, AdminUserRepository>();
        services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
        services.AddScoped<IEmailService, EmailService>();
        services.AddScoped<IAvatarStorageService, AvatarStorageService>();

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(
            typeof(UserAPI.Application.DTOs.UserProfileDto).Assembly));

        return services;
    }

    public static WebApplication UseUserModule(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetService<UserDbContext>();
        db?.Database.Migrate();

        return app;
    }
}
