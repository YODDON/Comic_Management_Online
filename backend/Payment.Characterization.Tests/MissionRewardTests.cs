using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using MissionAPI.Application.Features.Missions.Commands;
using MissionAPI.Entities;
using MissionAPI.Interfaces;
using Payment.Characterization.Tests.Infrastructure;
using SharedKernel.Enums;
using SharedKernel.Events;

namespace Payment.Characterization.Tests;

/// <summary>
/// T44: <c>CompleteMissionStepCommandHandler</c> must publish <c>MissionRewardGrantedEvent</c> only after the mission
/// is saved as complete. The host bus has no EF Outbox (T22 option A), so a publish goes out at once: publishing before
/// a save that then fails credited the coins while the mission stayed not complete.
/// The handler is the real one; the mission store is a fake (no MissionDB in this test project) and the event goes
/// through the world's bus to the real Wallet consumer. Production injects the scoped <c>IPublishEndpoint</c>, which
/// outside a consume context publishes straight to the bus, the same as <c>IBus</c> here.
/// </summary>
public abstract class MissionRewardScenarios : RedeliveryScenarios
{
    /// <summary>One mission and one user progress row; "completed" counts only once a save succeeded.</summary>
    private sealed class FakeMissionRepository : IMissionRepository
    {
        private readonly Mission _mission;
        private readonly UserMission _progress;
        public bool FailSave { get; init; }
        public bool SavedAsCompleted { get; private set; }
        public int Saves { get; private set; }

        public FakeMissionRepository(Mission mission, UserMission progress)
        {
            _mission = mission;
            _progress = progress;
        }

        public Task<Mission?> GetMissionByIdAsync(Guid id) => Task.FromResult(id == _mission.Id ? _mission : null);

        public Task<UserMission?> GetUserMissionAsync(int userId, Guid missionId) =>
            Task.FromResult(userId == _progress.UserId && missionId == _progress.MissionId ? _progress : null);

        public Task UpdateUserMissionAsync(UserMission userMission) => Task.CompletedTask;

        public Task SaveChangesAsync()
        {
            if (FailSave) throw new InvalidOperationException("T44: simulated MissionDB save failure");
            Saves++;
            SavedAsCompleted = _progress.IsCompleted;
            return Task.CompletedTask;
        }

        public Task<IEnumerable<Mission>> GetAllMissionsAsync() => throw new NotSupportedException();
        public Task<IEnumerable<Mission>> GetAllMissionsForAdminAsync() => throw new NotSupportedException();
        public Task<Mission> CreateMissionAsync(Mission mission) => throw new NotSupportedException();
        public Task UpdateMissionAsync(Mission mission) => throw new NotSupportedException();
        public Task<IEnumerable<UserMission>> GetUserMissionsAsync(int userId) => throw new NotSupportedException();
        public Task<UserMission> AddUserMissionAsync(UserMission userMission) => throw new NotSupportedException();
        public Task<IReadOnlyList<UserMission>> RecordActivityAsync(int userId, MissionType type, Guid activityId, DateTime occurredAt) =>
            throw new NotSupportedException();
    }

    private static FakeMissionRepository ReadyToComplete(int user, bool failSave, out Mission mission)
    {
        mission = new Mission
        {
            Id = Guid.NewGuid(),
            Title = "T44 mission",
            Description = "T44",
            RewardCoin = 50m,
            Type = MissionType.ReadChapter,
            TargetCount = 3,
            IsActive = true,
            StartDate = DateTime.UtcNow.AddDays(-1),
        };
        var progress = new UserMission { Id = Guid.NewGuid(), UserId = user, MissionId = mission.Id, CurrentProgress = 3, Mission = mission };
        return new FakeMissionRepository(mission, progress) { FailSave = failSave };
    }

    /// <summary>Same formula as the handler's private CreateRewardReference (MD5 of missionId bytes + userId bytes).</summary>
    private static Guid RewardReference(int userId, Guid missionId) =>
        new(System.Security.Cryptography.MD5.HashData(missionId.ToByteArray().Concat(BitConverter.GetBytes(userId)).ToArray()));

    private static int RewardEventsConsumed(TestWorld world) =>
        world.Messages.Snapshot().Count(m => m == nameof(MissionRewardGrantedEvent));

    protected async Task Save_fails_no_reward_is_credited()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        await world.SeedWalletAsync(user, 500m);
        var repository = ReadyToComplete(user, failSave: true, out var mission);

        using (var scope = world.WalletScope())
        {
            var handler = new CompleteMissionStepCommandHandler(repository, scope.ServiceProvider.GetRequiredService<IBus>());
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                handler.Handle(new CompleteMissionStepCommand { UserId = user, MissionId = mission.Id }, CancellationToken.None));
        }

        Assert.False(repository.SavedAsCompleted);
        // Give a reward published by mistake time to reach the Wallet consumer before checking that nothing arrived.
        Assert.False(await TestWorld.EventuallyAsync(() => Task.FromResult(RewardEventsConsumed(world) > 0), TimeSpan.FromSeconds(3)),
            "MissionRewardGrantedEvent was consumed although the mission was not saved as complete.");
        Assert.Equal(500m, await world.BalanceAsync(user));
        var ledger = await world.LedgerAsync(user);
        Assert.Single(ledger); // seed only
    }

    protected async Task Save_succeeds_reward_is_credited_once_even_if_sent_again()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        await world.SeedWalletAsync(user, 500m);
        var repository = ReadyToComplete(user, failSave: false, out var mission);
        var referenceId = RewardReference(user, mission.Id);

        using var scope = world.WalletScope();
        var handler = new CompleteMissionStepCommandHandler(repository, scope.ServiceProvider.GetRequiredService<IBus>());
        var response = await handler.Handle(new CompleteMissionStepCommand { UserId = user, MissionId = mission.Id }, CancellationToken.None);

        Assert.True(response.Data?.IsCompleted);
        Assert.True(repository.SavedAsCompleted);
        Assert.Equal(1, repository.Saves);
        Assert.True(await TestWorld.EventuallyAsync(async () => await world.BalanceAsync(user) == 550m), "The reward was not credited.");

        // A re-sent reward (new MessageId, same ReferenceId and payload) must not credit again.
        var copy = new MissionRewardGrantedEvent
        {
            UserId = user,
            CoinAmount = mission.RewardCoin,
            ReferenceId = referenceId,
            Description = $"Mission reward: {mission.Title}",
        };
        await DeliverAsync(scope, "mission-reward-granted", copy, Guid.NewGuid());

        Assert.Equal(550m, await world.BalanceAsync(user));
        var ledger = await world.LedgerAsync(user);
        Assert.Equal(2, ledger.Count); // seed + one reward
        Assert.Equal((TransactionType.MissionReward, 50m, (Guid?)referenceId), ledger[1]);
        Assert.Equal(2, RewardEventsConsumed(world)); // the handler's event and the copy
    }
}

[Collection(T37Collection.Name)]
public class MissionRewardTests_InMemory : MissionRewardScenarios
{
    private readonly T37Fixture _fixture;
    public MissionRewardTests_InMemory(T37Fixture fixture) => _fixture = fixture;
    protected override Task<TestWorld> WorldAsync() => _fixture.InMemoryAsync();

    [Fact] public Task Save_fails_no_reward_is_credited_and_the_mission_stays_open() => Save_fails_no_reward_is_credited();
    [Fact] public Task Save_succeeds_reward_credited_once_even_if_sent_again() => Save_succeeds_reward_is_credited_once_even_if_sent_again();
}

[Collection(T37Collection.Name)]
public class MissionRewardTests_RabbitMq : MissionRewardScenarios
{
    private readonly T37Fixture _fixture;
    public MissionRewardTests_RabbitMq(T37Fixture fixture) => _fixture = fixture;
    protected override Task<TestWorld> WorldAsync() => _fixture.RabbitMqAsync();

    [RabbitMqFact] public Task Save_fails_no_reward_is_credited_and_the_mission_stays_open() => Save_fails_no_reward_is_credited();
    [RabbitMqFact] public Task Save_succeeds_reward_credited_once_even_if_sent_again() => Save_succeeds_reward_is_credited_once_even_if_sent_again();
}

/// <summary>The monolith host's single RabbitMQ bus without outbox (T20).</summary>
[Collection(T37Collection.Name)]
public class MissionRewardTests_HostBusRabbitMq : MissionRewardScenarios
{
    private readonly T37Fixture _fixture;
    public MissionRewardTests_HostBusRabbitMq(T37Fixture fixture) => _fixture = fixture;
    protected override Task<TestWorld> WorldAsync() => _fixture.HostBusAsync();

    [RabbitMqFact] public Task Save_fails_no_reward_is_credited_and_the_mission_stays_open() => Save_fails_no_reward_is_credited();
    [RabbitMqFact] public Task Save_succeeds_reward_credited_once_even_if_sent_again() => Save_succeeds_reward_is_credited_once_even_if_sent_again();
}
