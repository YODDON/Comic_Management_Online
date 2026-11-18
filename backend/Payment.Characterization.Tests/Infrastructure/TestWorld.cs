extern alias walletserver;
extern alias chapterserver;

using AutoMapper;
using ChapterAPI.Application.Consumers;
using ChapterAPI.Data;
using ChapterAPI.Interfaces;
using ChapterAPI.Repositories;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentAPI.Application.Consumers;
using PaymentAPI.Application.Features.Payments.Commands;
using PaymentAPI.Application.Sagas;
using PaymentAPI.Data;
using PaymentAPI.Domain.Entities;
using PaymentAPI.Interfaces;
using PaymentAPI.Repositories;
using PaymentAPI.Settings;
using SharedKernel.Contracts.Purchase;
using SharedKernel.Enums;
using WalletAPI.Application.Consumers;
using WalletAPI.Data;
using WalletAPI.Interfaces;
using WalletAPI.Repositories;

namespace Payment.Characterization.Tests.Infrastructure;

public enum Topology
{
    /// <summary>
    /// What the code runs as today: three separate hosts (Payment, Wallet, Chapter), each with its own bus,
    /// its own EF Outbox and consumers, talking over RabbitMQ. Mirrors each module's Program.cs.
    /// </summary>
    SeparateBusesOnRabbitMq,

    /// <summary>
    /// The target of T20/T21: one host, one in-memory bus, no outbox. Only used to show whether the saga
    /// behaves the same without RabbitMQ. Since T20 the consumers, saga and endpoints come from the modules'
    /// own AddXxxConsumers / ConfigureXxxEndpoints, the methods the monolith host calls.
    /// </summary>
    SingleInMemoryBus,

    /// <summary>
    /// The monolith host after T20: one bus on RabbitMQ, no outbox, consumers, saga and endpoints registered by
    /// the modules' AddXxxConsumers / ConfigureXxxEndpoints (Payment, Wallet, Chapter; the host also has Comic and
    /// Mission consumers, which are not part of a purchase).
    /// </summary>
    SingleHostBusOnRabbitMq,
}

public sealed class WorldOptions
{
    public Topology Topology { get; init; } = Topology.SingleInMemoryBus;
}

/// <summary>Records the order messages are consumed in, across every bus of the world.</summary>
public sealed class MessageLog : IConsumeObserver
{
    private readonly List<string> _messages = new();
    public IReadOnlyList<string> Snapshot() { lock (_messages) return _messages.ToList(); }
    public void Clear() { lock (_messages) _messages.Clear(); }

    public Task PreConsume<T>(ConsumeContext<T> context) where T : class
    {
        lock (_messages) _messages.Add(typeof(T).Name);
        return Task.CompletedTask;
    }

    public Task PostConsume<T>(ConsumeContext<T> context) where T : class => Task.CompletedTask;
    public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class => Task.CompletedTask;
}

/// <summary>Records every state the PurchaseStateMachine moves to, as "Previous->Current".</summary>
public sealed class StateLog : IStateObserver<PurchaseState>
{
    private readonly List<(Guid Id, string Transition)> _transitions = new();

    public IReadOnlyList<string> All() { lock (_transitions) return _transitions.Select(t => t.Transition).ToList(); }
    public void Clear() { lock (_transitions) _transitions.Clear(); }

    public IReadOnlyList<string> For(Guid correlationId)
    {
        lock (_transitions) return _transitions.Where(t => t.Id == correlationId).Select(t => t.Transition).ToList();
    }

    public Task PreStateChange(BehaviorContext<PurchaseState> context, State currentState, State targetState) => Task.CompletedTask;

    public Task StateChanged(BehaviorContext<PurchaseState> context, State currentState, State previousState)
    {
        lock (_transitions) _transitions.Add((context.Saga.CorrelationId, $"{previousState?.Name ?? "(none)"}->{currentState.Name}"));
        return Task.CompletedTask;
    }

    public Task StateChangeFaulted(BehaviorContext<PurchaseState> context, State currentState, State targetState, Exception exception) => Task.CompletedTask;
}

/// <summary>
/// Hosts the Payment, Wallet and Chapter pieces of a chapter purchase / deposit with the REAL handlers, consumers,
/// saga, repositories and gRPC server classes, against the throwaway databases. Only the network hop of gRPC is
/// replaced (in-process call), and non-payment collaborators (Cloudinary, comic validation, mission notifier) are no-ops.
/// </summary>
public sealed class TestWorld : IAsyncDisposable
{
    public static string RabbitMqHost => Environment.GetEnvironmentVariable("T37_RABBITMQ_HOST") ?? "localhost";
    public static int RabbitMqPort => int.TryParse(Environment.GetEnvironmentVariable("T37_RABBITMQ_PORT"), out var port) ? port : 5672;

    public static bool RabbitMqReachable()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            return client.ConnectAsync(RabbitMqHost, RabbitMqPort).Wait(TimeSpan.FromMilliseconds(1500)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private readonly List<IHost> _hosts = new();
    private IHost _payment = null!, _wallet = null!, _chapter = null!;

    public WorldOptions Options { get; }
    /// <summary>Call log and failure switch for the Payment to Chapter/Wallet calls (name kept from the gRPC days, T17).</summary>
    public ModuleCallLog Grpc { get; } = new();
    public PaymentAPI.Settings.PurchaseRecoverySettings RecoverySettings { get; } = new();
    public MessageLog Messages { get; } = new();
    public StateLog States { get; } = new();

    /// <summary>When set, UnlockChapterAsync on the Chapter repository throws (a "database failed" injection).</summary>
    public volatile bool FailChapterUnlock;

    /// <summary>When set, DebitAsync on the Wallet repository throws (a "database failed" injection).</summary>
    public volatile bool FailWalletDebit;

    /// <summary>T43: when set, SaveChangesAsync on the Chapter repository throws (the unlock row is never saved).</summary>
    public volatile bool FailChapterSave;

    /// <summary>
    /// T43: runs once, synchronously, right before the next UnlockChapterAsync / SaveChangesAsync call on the Chapter
    /// repository, then clears itself. Lets a test change the database at that exact point (delete the chapter, or
    /// save a competing unlock row).
    /// </summary>
    public Action? BeforeChapterUnlock;
    public Action? BeforeChapterSave;

    private TestWorld(WorldOptions options) => Options = options;

    public static async Task<TestWorld> StartAsync(TestDatabases dbs, WorldOptions options)
    {
        var world = new TestWorld(options);
        await world.BuildAsync(dbs);
        return world;
    }

    // ---------- scopes ----------
    public IServiceScope PaymentScope() => _payment.Services.CreateScope();
    public IServiceScope WalletScope() => _wallet.Services.CreateScope();
    public IServiceScope ChapterScope() => _chapter.Services.CreateScope();

    // ---------- building ----------
    private async Task BuildAsync(TestDatabases dbs)
    {
        if (Options.Topology is Topology.SingleInMemoryBus or Topology.SingleHostBusOnRabbitMq)
        {
            var host = Host(services =>
            {
                AddPayment(services, dbs);
                AddWallet(services, dbs);
                AddChapter(services, dbs);
                services.AddMassTransit(x =>
                {
                    // The modules' own registrations, as called by ComicMonolith.Api/Program.cs (T20).
                    PaymentAPI.PaymentModule.AddPaymentConsumers(x);
                    walletserver::WalletAPI.WalletModule.AddWalletConsumers(x);
                    chapterserver::ChapterAPI.ChapterModule.AddChapterConsumers(x);

                    void Endpoints(IBusFactoryConfigurator cfg, IBusRegistrationContext ctx)
                    {
                        PaymentAPI.PaymentModule.ConfigurePaymentEndpoints(cfg, ctx);
                        walletserver::WalletAPI.WalletModule.ConfigureWalletEndpoints(cfg, ctx);
                        chapterserver::ChapterAPI.ChapterModule.ConfigureChapterEndpoints(cfg, ctx);
                    }

                    if (Options.Topology == Topology.SingleHostBusOnRabbitMq)
                        x.UsingRabbitMq((ctx, cfg) => { RabbitHost(cfg); Endpoints(cfg, ctx); });
                    else
                        x.UsingInMemory((ctx, cfg) => Endpoints(cfg, ctx));
                });
            });
            _payment = _wallet = _chapter = host;
        }
        else
        {
            _payment = Host(services =>
            {
                AddPayment(services, dbs);
                services.AddMassTransit(x =>
                {
                    AddPaymentBus(x, outbox: true);
                    x.UsingRabbitMq((ctx, cfg) => { RabbitHost(cfg); PaymentEndpoints(cfg, ctx); });
                });
            });
            _wallet = Host(services =>
            {
                AddWallet(services, dbs);
                services.AddMassTransit(x =>
                {
                    AddWalletBus(x, outbox: true);
                    x.UsingRabbitMq((ctx, cfg) => { RabbitHost(cfg); WalletEndpoints(cfg, ctx); });
                });
            });
            _chapter = Host(services =>
            {
                AddChapter(services, dbs);
                services.AddMassTransit(x =>
                {
                    AddChapterBus(x, outbox: true);
                    x.UsingRabbitMq((ctx, cfg) => { RabbitHost(cfg); ChapterEndpoints(cfg, ctx); });
                });
            });
        }


        foreach (var host in _hosts)
        {
            await host.StartAsync();
            host.Services.GetRequiredService<IBus>().ConnectConsumeObserver(Messages);
        }

        _payment.Services.GetRequiredService<PurchaseStateMachine>().ConnectStateObserver(States);
    }

    private IHost Host(Action<IServiceCollection> configure)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddLogging();
        // Start returns only once the bus and its receive endpoints (and their RabbitMQ bindings) are ready.
        // Without it, a test that publishes right after start on a fresh broker loses the message (unroutable).
        builder.Services.Configure<MassTransitHostOptions>(o => o.WaitUntilStarted = true);
        configure(builder.Services);
        var host = builder.Build();
        _hosts.Add(host);
        return host;
    }

    private static void RabbitHost(IRabbitMqBusFactoryConfigurator cfg) =>
        cfg.Host(RabbitMqHost, (ushort)RabbitMqPort, "/", h => { h.Username("guest"); h.Password("guest"); });

    private void AddPayment(IServiceCollection s, TestDatabases dbs)
    {
        s.AddDbContext<PaymentDbContext>(o => o.UseSqlServer(dbs.PaymentConnection));
        s.AddScoped<IPaymentRepository, PaymentRepository>();
        s.AddSingleton(Microsoft.Extensions.Options.Options.Create(DefaultBank));
        s.AddSingleton(Microsoft.Extensions.Options.Options.Create(new TopUpSettings()));
        // The REAL gateways of the Chapter and Wallet modules, each running in its own host/container as the gRPC servers did.
        s.AddSingleton<SharedKernel.Contracts.Modules.IPaymentChapterGateway>(_ => new RecordingChapterGateway(
            new chapterserver::ChapterAPI.ModuleServices.PaymentChapterGateway(_chapter.Services.GetRequiredService<IServiceScopeFactory>()), Grpc));
        s.AddSingleton<SharedKernel.Contracts.Modules.IPaymentWalletGateway>(_ => new RecordingWalletGateway(
            new walletserver::WalletAPI.ModuleServices.PaymentWalletGateway(_wallet.Services.GetRequiredService<IServiceScopeFactory>()), Grpc));
        s.AddMediatR(c => c.RegisterServicesFromAssemblies(typeof(PurchaseChapterCommandHandler).Assembly));
        // T23: the purchase recovery job, as AddPaymentModule registers it, but NOT its hosted service: tests call
        // RunOnceAsync themselves. RecoverySettings can be changed by a test before the first run.
        s.AddSingleton<IOptions<PaymentAPI.Settings.PurchaseRecoverySettings>>(_ => Microsoft.Extensions.Options.Options.Create(RecoverySettings));
        s.AddSingleton<PaymentAPI.Application.Services.PurchaseRecoveryJob>();
    }

    private void AddWallet(IServiceCollection s, TestDatabases dbs)
    {
        s.AddDbContext<WalletDbContext>(o => o.UseSqlServer(dbs.WalletConnection));
        s.AddScoped<IWalletRepository>(sp =>
            FaultInjectingProxy.Wrap<IWalletRepository>(
                ActivatorUtilities.CreateInstance<WalletRepository>(sp),
                (method, _) => FailWalletDebit && method.Name == nameof(IWalletRepository.DebitAsync)
                    ? new InvalidOperationException("injected: wallet database failed")
                    : null));
        s.AddMediatR(c => c.RegisterServicesFromAssemblies(typeof(WalletAPI.Application.Features.Wallet.Commands.AddCoinCommand).Assembly));
    }

    private void AddChapter(IServiceCollection s, TestDatabases dbs)
    {
        s.AddDbContext<ChapterDbContext>(o => o.UseSqlServer(dbs.ChapterConnection));
        s.AddScoped<IChapterRepository>(sp =>
            FaultInjectingProxy.Wrap<IChapterRepository>(
                ActivatorUtilities.CreateInstance<ChapterRepository>(sp),
                (method, _) =>
                {
                    if (method.Name == nameof(IChapterRepository.UnlockChapterAsync))
                    {
                        Interlocked.Exchange(ref BeforeChapterUnlock, null)?.Invoke();
                        return FailChapterUnlock ? new InvalidOperationException("injected: chapter database failed") : null;
                    }

                    if (method.Name == nameof(IChapterRepository.SaveChangesAsync))
                    {
                        Interlocked.Exchange(ref BeforeChapterSave, null)?.Invoke();
                        return FailChapterSave ? new InvalidOperationException("injected: chapter save failed") : null;
                    }

                    return null;
                }));
        // Not under test: the unlock handler needs these only for mission progress / comic checks.
        s.AddSingleton<IMapper>(new MapperConfiguration(_ => { }).CreateMapper());
        s.AddSingleton(NoOpProxy.Create<IComicValidator>());
        s.AddSingleton(NoOpProxy.Create<ICloudinaryService>());
        s.AddSingleton(NoOpProxy.Create<IMissionProgressNotifier>());
        s.AddMediatR(c => c.RegisterServicesFromAssemblies(typeof(UnlockChapterConsumer).Assembly));
    }

    public static BankSettings DefaultBank => new()
    {
        BankCode = "VCB",
        AccountNumber = "0123456789",
        AccountName = "TEST ACCOUNT",
        SepayApiKey = "t37-sepay-key",
    };

    // ---------- MassTransit pieces, copied from each module's Program.cs (names, order, outbox) ----------
    private void AddPaymentBus(IBusRegistrationConfigurator x, bool outbox)
    {
        if (outbox)
            x.AddEntityFrameworkOutbox<PaymentDbContext>(o => { o.UseSqlServer(); o.UseBusOutbox(); });
        x.AddConsumer<PurchaseCompletedConsumer>();
        x.AddConsumer<PurchaseFailedConsumer>();
        x.AddSagaStateMachine<PurchaseStateMachine, PurchaseState>()
            .EntityFrameworkRepository(r => { r.ExistingDbContext<PaymentDbContext>(); r.UseSqlServer(); });
        // No AddRequestClient: production relies on MassTransit's default request client (30 s timeout).
    }

    private static void PaymentEndpoints(IBusFactoryConfigurator cfg, IBusRegistrationContext ctx)
    {
        cfg.ReceiveEndpoint("payment-purchase-completed", e => e.ConfigureConsumer<PurchaseCompletedConsumer>(ctx));
        cfg.ReceiveEndpoint("payment-purchase-failed", e => e.ConfigureConsumer<PurchaseFailedConsumer>(ctx));
        cfg.ReceiveEndpoint("purchase-state", e => e.ConfigureSaga<PurchaseState>(ctx));
    }

    private static void AddWalletBus(IBusRegistrationConfigurator x, bool outbox)
    {
        if (outbox)
            x.AddEntityFrameworkOutbox<WalletDbContext>(o => { o.UseSqlServer(); o.UseBusOutbox(); });
        // Added by T20 (RedeliveryTests): WalletAPI's Program.cs registers this consumer too.
        x.AddConsumer<walletserver::WalletAPI.Consumers.MissionRewardGrantedConsumer>();
        x.AddConsumer<DebitWalletConsumer>();
        x.AddConsumer<RefundWalletConsumer>();
    }

    private static void WalletEndpoints(IBusFactoryConfigurator cfg, IBusRegistrationContext ctx)
    {
        cfg.ReceiveEndpoint("mission-reward-granted", e => e.ConfigureConsumer<walletserver::WalletAPI.Consumers.MissionRewardGrantedConsumer>(ctx));
        cfg.ReceiveEndpoint("debit-wallet", e => e.ConfigureConsumer<DebitWalletConsumer>(ctx));
        cfg.ReceiveEndpoint("refund-wallet", e => e.ConfigureConsumer<RefundWalletConsumer>(ctx));
    }

    private static void AddChapterBus(IBusRegistrationConfigurator x, bool outbox)
    {
        if (outbox)
            x.AddEntityFrameworkOutbox<ChapterDbContext>(o => { o.UseSqlServer(); o.UseBusOutbox(); });
        x.AddConsumer<UnlockChapterConsumer>();
    }

    private static void ChapterEndpoints(IBusFactoryConfigurator cfg, IBusRegistrationContext ctx)
    {
        cfg.ReceiveEndpoint("unlock-chapter", e => e.ConfigureConsumer<UnlockChapterConsumer>(ctx));
    }

    // ---------- seeding and reading (through the real repositories / contexts) ----------
    private static int _nextUser = Random.Shared.Next(100_000, 900_000);
    public static int NewUserId() => Interlocked.Increment(ref _nextUser);

    public async Task SeedWalletAsync(int userId, decimal coins)
    {
        using var scope = WalletScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWalletRepository>();
        await repository.CreditAsync(userId, coins, Guid.NewGuid(), TransactionType.ManualTopUp, "T37 seed");
    }

    public async Task<Guid> SeedChapterAsync(decimal price, string status = "Published")
    {
        using var scope = ChapterScope();
        var db = scope.ServiceProvider.GetRequiredService<ChapterDbContext>();
        var chapter = new ChapterAPI.Entities.Chapter
        {
            ComicId = Guid.NewGuid(),
            Title = "T37 chapter",
            Slug = $"t37-{Guid.NewGuid():N}",
            ChapterNumber = 1,
            UnitPrice = price,
            Status = status,
        };
        db.Chapters.Add(chapter);
        await db.SaveChangesAsync();
        return chapter.Id;
    }

    public async Task<decimal?> BalanceAsync(int userId)
    {
        using var scope = WalletScope();
        var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
        return await db.Wallets.Where(w => w.UserId == userId).Select(w => (decimal?)w.Balance).SingleOrDefaultAsync();
    }

    public async Task<List<(TransactionType Type, decimal Amount, Guid? ReferenceId)>> LedgerAsync(int userId)
    {
        using var scope = WalletScope();
        var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
        var rows = await db.CurrencyEntries.Where(e => e.Wallet.UserId == userId).OrderBy(e => e.CreatedAt).ToListAsync();
        return rows.Select(e => (e.Type, e.Amount, e.ReferenceId)).ToList();
    }

    public async Task<bool> ChapterUnlockedAsync(int userId, Guid chapterId)
    {
        using var scope = ChapterScope();
        var db = scope.ServiceProvider.GetRequiredService<ChapterDbContext>();
        return await db.UserPurchases.AnyAsync(p => p.UserId == userId && p.ChapterId == chapterId);
    }

    public async Task<List<PaymentAPI.Entities.Transaction>> TransactionsAsync(int userId)
    {
        using var scope = PaymentScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        return await db.Transactions.AsNoTracking().Where(t => t.UserId == userId).OrderBy(t => t.CreatedAt).ToListAsync();
    }

    public async Task<PaymentAPI.Entities.UserPurchase?> PaymentPurchaseAsync(int userId, Guid chapterId)
    {
        using var scope = PaymentScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        return await db.UserPurchases.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId && p.ChapterId == chapterId);
    }

    public async Task<PurchaseState?> SagaAsync(Guid correlationId)
    {
        using var scope = PaymentScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        return await db.PurchaseStates.AsNoTracking().FirstOrDefaultAsync(s => s.CorrelationId == correlationId);
    }

    public async Task<List<PurchaseState>> SagasForAsync(int userId)
    {
        using var scope = PaymentScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        return await db.PurchaseStates.AsNoTracking().Where(s => s.UserId == userId).ToListAsync();
    }

    /// <summary>Polls until the condition holds (background saga work) or the timeout passes. Returns whether it held.</summary>
    public static async Task<bool> EventuallyAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(100);
        }
        return await condition();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            try { await host.StopAsync(TimeSpan.FromSeconds(10)); } catch { /* best effort */ }
            host.Dispose();
        }
    }
}
