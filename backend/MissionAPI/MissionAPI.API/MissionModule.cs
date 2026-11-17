using ChapterAPI.Protos;
using Microsoft.EntityFrameworkCore;
using MissionAPI.Controllers;
using MissionAPI.Data;
using SocialAPI.Protos;

namespace MissionAPI;

public static class MissionModule
{
    public static IServiceCollection AddMissionModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllers()
            .AddApplicationPart(typeof(MissionsController).Assembly);

        var missionConn = Environment.GetEnvironmentVariable("MISSION_DB_CONNECTION")
            ?? configuration.GetConnectionString("MissionConnection");
        if (!string.IsNullOrEmpty(missionConn))
        {
            services.AddDbContext<MissionDbContext>(options => options.UseSqlServer(missionConn));
        }

        services.AddScoped<MissionAPI.Interfaces.IMissionRepository, MissionAPI.Repositories.MissionRepository>();
        services.AddScoped<MissionAPI.Interfaces.INotificationRepository, MissionAPI.Repositories.NotificationRepository>();
        services.AddScoped<MissionAPI.Interfaces.IUploadRepository, MissionAPI.Repositories.UploadRepository>();
        services.AddScoped<MissionAPI.Interfaces.IUploadService, MissionAPI.Services.UploadService>();

        services.AddScoped<MissionAPI.Interfaces.ICloudinaryService, MissionAPI.Services.CloudinaryService>();
        services.Configure<MissionAPI.Settings.CloudinarySettings>(options =>
        {
            options.CloudName = Environment.GetEnvironmentVariable("Cloudinary__CloudName")
                ?? Environment.GetEnvironmentVariable("CloudinarySettings__CloudName")
                ?? configuration["Cloudinary:CloudName"]
                ?? "";
            options.ApiKey = Environment.GetEnvironmentVariable("Cloudinary__ApiKey")
                ?? Environment.GetEnvironmentVariable("CloudinarySettings__ApiKey")
                ?? configuration["Cloudinary:ApiKey"]
                ?? "";
            options.ApiSecret = Environment.GetEnvironmentVariable("Cloudinary__ApiSecret")
                ?? Environment.GetEnvironmentVariable("CloudinarySettings__ApiSecret")
                ?? configuration["Cloudinary:ApiSecret"]
                ?? "";
        });

        // Still gRPC (replaced in T16-T18). The host does not serve gRPC (ChapterGrpcService and
        // SocialActivityGrpcService are not mapped), so SyncCommand fails in the host until then.
        // See "Remaining gRPC calls" in PLAN.md.
        var chapterApiUrl = Environment.GetEnvironmentVariable("CHAPTER_API_URL")
            ?? configuration["GrpcEndpoints:ChapterAPI"]
            ?? "https://localhost:7114";
        // Explicit client name: the default name is the type name without namespace ("ChapterGrpcClient"),
        // which clashes with ComicAPI's own generated ChapterGrpcClient once both modules are in the same host.
        services.AddGrpcClient<ChapterGrpc.ChapterGrpcClient>("MissionAPI.ChapterGrpcClient", o => o.Address = new Uri(chapterApiUrl));

        var socialApiUrl = Environment.GetEnvironmentVariable("SOCIAL_API_URL")
            ?? configuration["GrpcEndpoints:SocialAPI"]
            ?? "https://localhost:7133";
        services.AddGrpcClient<SocialActivity.SocialActivityClient>(o => o.Address = new Uri(socialApiUrl));

        // Not registered here: MassTransit, the EF Outbox and MissionActivityRecordedConsumer
        // (the host owns the single bus; the consumer does not run in the host until T20),
        // and the gRPC server (MissionProgressGrpcService is not mapped).

        return services;
    }

    public static WebApplication UseMissionModule(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetService<MissionDbContext>();
        db?.Database.Migrate();
        if (db != null && !db.Missions.Any())
        {
            db.Missions.AddRange(
                new MissionAPI.Entities.Mission { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Title = "Đọc Truyện Chăm Chỉ", Description = "Đọc ít nhất 5 chương truyện bất kỳ.", RewardCoin = 50, Type = SharedKernel.Enums.MissionType.ReadChapter, TargetCount = 5, IsActive = true, StartDate = new DateTime(2026, 1, 1), CreatedAt = new DateTime(2026, 1, 1) },
                new MissionAPI.Entities.Mission { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), Title = "Mua Chapter", Description = "Mua ít nhất một chapter.", RewardCoin = 200, Type = SharedKernel.Enums.MissionType.PurchaseChapter, TargetCount = 1, IsActive = true, StartDate = new DateTime(2026, 1, 1), CreatedAt = new DateTime(2026, 1, 1) },
                new MissionAPI.Entities.Mission { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), Title = "Nhà Phê Bình", Description = "Bình luận ít nhất 3 lần.", RewardCoin = 30, Type = SharedKernel.Enums.MissionType.LeaveComment, TargetCount = 3, IsActive = true, StartDate = new DateTime(2026, 1, 1), CreatedAt = new DateTime(2026, 1, 1) });
            db.SaveChanges();
        }

        return app;
    }
}
