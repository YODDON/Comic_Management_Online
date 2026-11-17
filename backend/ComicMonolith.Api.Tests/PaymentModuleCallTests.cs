using System.Collections.Concurrent;
using ChapterAPI.Application.Features.Chapters.Commands;
using ChapterAPI.Application.Features.Chapters.Queries;
using ChapterAPI.Entities;
using ChapterAPI.ModuleServices;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using SharedKernel.Contracts.Modules;
using WalletAPI.Application.Features.Wallet.Commands;
using WalletAPI.DTOs;
using WalletAPI.ModuleServices;

namespace ComicMonolith.Api.Tests;

/// <summary>
/// T17 group 3: PaymentAPI to ChapterAPI and PaymentAPI to WalletAPI, now direct in-process calls. Money and
/// chapter data must arrive exactly as before, every failure must become <see cref="ModuleCallException"/>
/// (the callers' catch blocks), and every call must run in its own DI scope (Payment calls GetChapterInfo
/// concurrently, and one scope would share one DbContext between those calls).
/// </summary>
public class PaymentModuleCallTests
{
    // ---- ChapterAPI side: PaymentChapterGateway ----

    [Fact]
    public async Task Chapter_info_maps_every_field_without_rounding_the_price()
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var probe = new Probe(request =>
        {
            Assert.Equal(chapterId, ((GetChapterInfoQuery)request).ChapterId);
            return new Chapter
            {
                ComicId = comicId, UnitPrice = 12345.67m, Title = "Title", ChapterNumber = 7, Slug = "slug", Status = "Published"
            };
        });

        var info = await new PaymentChapterGateway(probe.ScopeFactory()).GetChapterInfoAsync(chapterId);

        Assert.Equal(new ChapterInfo(true, comicId, 12345.67m, "Title", 7, "slug", "Published"), info);
    }

    [Fact]
    public async Task Chapter_info_for_a_missing_chapter_is_the_not_found_answer_not_an_error()
    {
        var probe = new Probe(_ => null!);

        var info = await new PaymentChapterGateway(probe.ScopeFactory()).GetChapterInfoAsync(Guid.NewGuid());

        Assert.False(info.Exists);
        Assert.Same(ChapterInfo.NotFound, info);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Unlock_forwards_the_ids_and_maps_both_flags(bool success, bool alreadyPurchased)
    {
        var chapterId = Guid.NewGuid();
        var probe = new Probe(request =>
        {
            var command = (UnlockChapterCommand)request;
            Assert.Equal(42, command.UserId);
            Assert.Equal(chapterId, command.ChapterId);
            return (success, alreadyPurchased);
        });

        var result = await new PaymentChapterGateway(probe.ScopeFactory()).UnlockChapterAsync(42, chapterId);

        Assert.Equal(new UnlockChapterResult(success, alreadyPurchased), result);
    }

    [Fact]
    public async Task Chapter_gateway_wraps_any_failure_and_keeps_the_cause()
    {
        var cause = new InvalidOperationException("db down");
        var gateway = new PaymentChapterGateway(new Probe(_ => throw cause).ScopeFactory());

        var info = await Assert.ThrowsAsync<ModuleCallException>(() => gateway.GetChapterInfoAsync(Guid.NewGuid()));
        var unlock = await Assert.ThrowsAsync<ModuleCallException>(() => gateway.UnlockChapterAsync(1, Guid.NewGuid()));

        Assert.Same(cause, info.InnerException);
        Assert.Same(cause, unlock.InnerException);
    }

    [Fact]
    public async Task Chapter_gateway_does_not_wrap_cancellation()
    {
        var gateway = new PaymentChapterGateway(new Probe(_ => throw new OperationCanceledException()).ScopeFactory());

        await Assert.ThrowsAsync<OperationCanceledException>(() => gateway.GetChapterInfoAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<OperationCanceledException>(() => gateway.UnlockChapterAsync(1, Guid.NewGuid()));
    }

    [Fact]
    public async Task Chapter_gateway_gives_every_call_its_own_scope_even_when_calls_overlap()
    {
        var probe = new Probe(_ => new Chapter { Status = "Published" });
        var gateway = new PaymentChapterGateway(probe.ScopeFactory());

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => gateway.GetChapterInfoAsync(Guid.NewGuid())));

        Assert.Equal(20, probe.ScopeIds.Distinct().Count());
        Assert.Equal(20, probe.ScopesDisposed);
    }

    // ---- WalletAPI side: PaymentWalletGateway ----

    [Fact]
    public async Task Add_coin_sends_the_decimal_amount_unchanged_and_all_other_fields()
    {
        var reference = Guid.NewGuid();
        var probe = new Probe(request =>
        {
            var command = (AddCoinCommand)request;
            Assert.Equal(9, command.UserId);
            Assert.Equal(12345.67m, command.Amount);
            Assert.Equal(reference, command.ReferenceId);
            Assert.Equal("Top-up UP123", command.Description);
            Assert.Equal("ManualTopUp", command.CreditType);
            return new WalletCreditResultDto(false, 99m);
        });

        var result = await new PaymentWalletGateway(probe.ScopeFactory())
            .AddCoinAsync(9, 12345.67m, reference, "Top-up UP123", "ManualTopUp");

        Assert.Equal(new WalletCreditResult(true, "Coins added successfully."), result);
    }

    [Fact]
    public async Task Add_coin_answers_success_for_an_already_processed_reference()
    {
        var probe = new Probe(_ => new WalletCreditResultDto(true, 99m));

        var result = await new PaymentWalletGateway(probe.ScopeFactory())
            .AddCoinAsync(9, 10m, Guid.NewGuid(), "d", "ManualTopUp");

        Assert.Equal(new WalletCreditResult(true, "Already processed."), result);
    }

    [Fact]
    public async Task Add_coin_accepts_an_empty_reference_like_the_grpc_server_did()
    {
        var ran = false;
        var probe = new Probe(_ => { ran = true; return new WalletCreditResultDto(false, 1m); });

        await new PaymentWalletGateway(probe.ScopeFactory()).AddCoinAsync(9, 10m, Guid.Empty, "d", "ManualTopUp");

        Assert.True(ran);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(9, 0)]
    [InlineData(9, -5)]
    public async Task Add_coin_rejects_a_user_id_or_amount_of_zero_or_less_without_calling_the_command(int userId, int amount)
    {
        var ran = false;
        var probe = new Probe(_ => { ran = true; return new WalletCreditResultDto(false, 1m); });
        var gateway = new PaymentWalletGateway(probe.ScopeFactory());

        await Assert.ThrowsAsync<ModuleCallException>(() => gateway.AddCoinAsync(userId, amount, Guid.NewGuid(), "d", "ManualTopUp"));

        Assert.False(ran);
    }

    [Fact]
    public async Task Wallet_gateway_wraps_any_failure_and_keeps_the_cause()
    {
        var cause = new InvalidOperationException("serialization failure");
        var gateway = new PaymentWalletGateway(new Probe(_ => throw cause).ScopeFactory());

        var thrown = await Assert.ThrowsAsync<ModuleCallException>(
            () => gateway.AddCoinAsync(9, 10m, Guid.NewGuid(), "d", "ManualTopUp"));

        Assert.Same(cause, thrown.InnerException);
    }

    [Fact]
    public async Task Wallet_gateway_does_not_wrap_cancellation()
    {
        var gateway = new PaymentWalletGateway(new Probe(_ => throw new OperationCanceledException()).ScopeFactory());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => gateway.AddCoinAsync(9, 10m, Guid.NewGuid(), "d", "ManualTopUp"));
    }

    [Fact]
    public async Task Wallet_gateway_gives_every_call_its_own_scope()
    {
        var probe = new Probe(_ => new WalletCreditResultDto(false, 1m));
        var gateway = new PaymentWalletGateway(probe.ScopeFactory());

        await gateway.AddCoinAsync(9, 10m, Guid.NewGuid(), "d", "ManualTopUp");
        await gateway.AddCoinAsync(9, 10m, Guid.NewGuid(), "d", "ManualTopUp");

        Assert.Equal(2, probe.ScopeIds.Distinct().Count());
        Assert.Equal(2, probe.ScopesDisposed);
    }

    // ---- helpers ----

    /// <summary>A service provider whose scoped IMediator answers with a function and records which scope served it.</summary>
    private sealed class Probe
    {
        private readonly ServiceProvider _provider;
        private int _disposed;

        public ConcurrentBag<Guid> ScopeIds { get; } = new();
        public int ScopesDisposed => _disposed;

        public Probe(Func<object, object> answer)
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => new ScopeMarker(this));
            services.AddScoped<IMediator>(sp =>
            {
                ScopeIds.Add(sp.GetRequiredService<ScopeMarker>().Id);
                return new FakeMediator(answer);
            });
            _provider = services.BuildServiceProvider();
        }

        public IServiceScopeFactory ScopeFactory() => _provider.GetRequiredService<IServiceScopeFactory>();

        private sealed class ScopeMarker : IDisposable
        {
            private readonly Probe _owner;
            public Guid Id { get; } = Guid.NewGuid();
            public ScopeMarker(Probe owner) => _owner = owner;
            public void Dispose() => Interlocked.Increment(ref _owner._disposed);
        }
    }

    private sealed class FakeMediator : IMediator
    {
        private readonly Func<object, object> _answer;
        public FakeMediator(Func<object, object> answer) => _answer = answer;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)_answer(request));

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
