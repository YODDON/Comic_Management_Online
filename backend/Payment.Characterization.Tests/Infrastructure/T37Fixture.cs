namespace Payment.Characterization.Tests.Infrastructure;

/// <summary>
/// One set of throwaway databases and (lazily) one world per topology for the whole test run.
/// Tests isolate themselves with fresh user ids, chapter ids and deposit codes, and reset fault switches.
/// </summary>
public sealed class T37Fixture : IAsyncLifetime
{
    private TestWorld? _inMemory;
    private TestWorld? _rabbit;
    private TestWorld? _hostBus;

    /// <summary>
    /// T20: set T37_WORLD=HostBus to run EVERY test (in-memory and RabbitMQ ones alike) on the host-style single
    /// RabbitMQ bus (needs a broker). Unset: each test uses the world its class asks for.
    /// </summary>
    private static bool AllOnHostBus =>
        string.Equals(Environment.GetEnvironmentVariable("T37_WORLD"), "HostBus", StringComparison.OrdinalIgnoreCase);

    public TestDatabases Dbs { get; } = new();

    public Task InitializeAsync() => Dbs.InitializeAsync();

    public async Task<TestWorld> InMemoryAsync() => AllOnHostBus
        ? await HostBusAsync()
        : Reset(_inMemory ??= await TestWorld.StartAsync(Dbs, new WorldOptions { Topology = Topology.SingleInMemoryBus }));

    public async Task<TestWorld> RabbitMqAsync()
    {
        if (AllOnHostBus) return await HostBusAsync();
        // The two RabbitMQ worlds bind the same queue names; only one may be running, or they would steal each other's messages.
        if (_hostBus != null) { await _hostBus.DisposeAsync(); _hostBus = null; }
        return Reset(_rabbit ??= await TestWorld.StartAsync(Dbs, new WorldOptions { Topology = Topology.SeparateBusesOnRabbitMq }));
    }

    /// <summary>The monolith host's single bus on RabbitMQ (T20).</summary>
    public async Task<TestWorld> HostBusAsync()
    {
        if (_rabbit != null) { await _rabbit.DisposeAsync(); _rabbit = null; }
        return Reset(_hostBus ??= await TestWorld.StartAsync(Dbs, new WorldOptions { Topology = Topology.SingleHostBusOnRabbitMq }));
    }

    private static TestWorld Reset(TestWorld world)
    {
        world.Grpc.Unavailable.Clear();
        world.Grpc.AfterUnlockCheck = null;
        world.FailChapterUnlock = false;
        world.FailWalletDebit = false;
        world.Messages.Clear();
        world.States.Clear();
        return world;
    }

    public async Task DisposeAsync()
    {
        if (_inMemory != null) await _inMemory.DisposeAsync();
        if (_rabbit != null) await _rabbit.DisposeAsync();
        if (_hostBus != null) await _hostBus.DisposeAsync();
        await Dbs.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class T37Collection : ICollectionFixture<T37Fixture>
{
    public const string Name = "T37";
}

/// <summary>Runs only when a RabbitMQ broker answers (T37_RABBITMQ_HOST and T37_RABBITMQ_PORT, default localhost:5672); otherwise reported as skipped.</summary>
public sealed class RabbitMqFactAttribute : FactAttribute
{
    public RabbitMqFactAttribute()
    {
        if (!TestWorld.RabbitMqReachable())
            Skip = $"No RabbitMQ broker on {TestWorld.RabbitMqHost}:{TestWorld.RabbitMqPort} (start one, e.g. a throwaway rabbitmq:3 container).";
    }
}
