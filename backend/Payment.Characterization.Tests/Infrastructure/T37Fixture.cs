namespace Payment.Characterization.Tests.Infrastructure;

/// <summary>
/// One set of throwaway databases and (lazily) one world per topology for the whole test run.
/// Tests isolate themselves with fresh user ids, chapter ids and deposit codes, and reset fault switches.
/// </summary>
public sealed class T37Fixture : IAsyncLifetime
{
    private TestWorld? _inMemory;
    private TestWorld? _rabbit;

    public TestDatabases Dbs { get; } = new();

    public Task InitializeAsync() => Dbs.InitializeAsync();

    public async Task<TestWorld> InMemoryAsync() =>
        Reset(_inMemory ??= await TestWorld.StartAsync(Dbs, new WorldOptions { Topology = Topology.SingleInMemoryBus }));

    public async Task<TestWorld> RabbitMqAsync() =>
        Reset(_rabbit ??= await TestWorld.StartAsync(Dbs, new WorldOptions { Topology = Topology.SeparateBusesOnRabbitMq }));

    private static TestWorld Reset(TestWorld world)
    {
        world.Grpc.Unavailable.Clear();
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
