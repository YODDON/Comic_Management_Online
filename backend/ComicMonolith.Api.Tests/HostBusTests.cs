using ChapterAPI;
using ComicAPI.API;
using ComicAPI.Application.Features.Comics.Commands;
using MassTransit;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using MissionAPI;
using PaymentAPI;
using SharedKernel.Events;
using WalletAPI;

namespace ComicMonolith.Api.Tests;

/// <summary>
/// T21: the host bus runs on the in-memory transport (no broker). Builds the same bus as
/// ComicMonolith.Api/Program.cs (the 5 modules' AddXxxConsumers / ConfigureXxxEndpoints on UsingInMemory)
/// and checks that a representative event, ComicViewedIntegrationEvent, reaches ComicViewedEventConsumer.
/// The consumer's only effect is sending IncrementComicViewCommand through MediatR, so a fake mediator records it.
/// </summary>
public class HostBusTests
{
    [Fact]
    public async Task ComicViewedIntegrationEvent_published_on_the_in_memory_host_bus_is_consumed()
    {
        var comicId = Guid.NewGuid();
        var received = new TaskCompletionSource<IncrementComicViewCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mediator = new FakeMediator(request =>
        {
            var command = Assert.IsType<IncrementComicViewCommand>(request);
            received.TrySetResult(command);
            return true;
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMediator>(mediator);
        services.AddMassTransit(x =>
        {
            x.AddChapterConsumers();
            x.AddComicConsumers();
            x.AddMissionConsumers();
            x.AddPaymentConsumers();
            x.AddWalletConsumers();

            x.UsingInMemory((context, cfg) =>
            {
                cfg.ConfigureChapterEndpoints(context);
                cfg.ConfigureComicEndpoints(context);
                cfg.ConfigureMissionEndpoints(context);
                cfg.ConfigurePaymentEndpoints(context);
                cfg.ConfigureWalletEndpoints(context);
            });
        });

        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBusControl>();
        await bus.StartAsync(TimeSpan.FromSeconds(10));
        try
        {
            await bus.Publish(new ComicViewedIntegrationEvent(comicId));

            var command = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(comicId, command.ComicId);
        }
        finally
        {
            await bus.StopAsync(TimeSpan.FromSeconds(10));
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
