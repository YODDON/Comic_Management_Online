using BannerAPI.Controllers;
using BannerAPI.Data;
using BannerAPI.Interfaces;
using BannerAPI.Repositories;
using BannerAPI.Services;
using Microsoft.EntityFrameworkCore;

namespace BannerAPI;

public static class BannerModule
{
    public static IServiceCollection AddBannerModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllers()
            .AddApplicationPart(typeof(BannersController).Assembly);

        services.AddScoped<IBannerRepository, BannerRepository>();
        services.AddScoped<ICloudinaryService, CloudinaryService>();

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(
            typeof(BannerAPI.Application.Features.Banners.Commands.CreateBannerCommand).Assembly));

        var bannerConn = Environment.GetEnvironmentVariable("BANNER_DB_CONNECTION")
            ?? configuration.GetConnectionString("BannerConnection")
            ?? configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrEmpty(bannerConn))
        {
            services.AddDbContext<BannerDbContext>(options => options.UseSqlServer(bannerConn));
        }

        return services;
    }

    public static WebApplication UseBannerModule(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetService<BannerDbContext>();
        db?.Database.Migrate();

        return app;
    }
}
