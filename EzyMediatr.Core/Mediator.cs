using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Internal;

[assembly: InternalsVisibleTo("EzyMediatr.DependencyInjection")]

namespace EzyMediatr.Core;

public class Mediator(IServiceProvider serviceProvider) : IMediator
{

    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var scope = serviceProvider.CreateScope();
        var wrapper = RequestHandlerWrapperFactory.Create(request.GetType(), typeof(TResponse));
        var result = await wrapper.Handle(request, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        return (TResponse)result!;
    }

    public async IAsyncEnumerable<TResponse> Stream<TResponse>(IStreamRequest<TResponse> request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var scope = serviceProvider.CreateScope();
        var wrapper = StreamRequestHandlerWrapperFactory.Create(request.GetType(), typeof(TResponse));

        await foreach (var item in wrapper.Handle(request, scope.ServiceProvider, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return (TResponse)item!;
        }
    }

    public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);

        using var scope = serviceProvider.CreateScope();
        var wrapper = NotificationHandlerWrapperFactory.Create(notification.GetType());
        await wrapper.Handle(notification, scope.ServiceProvider, cancellationToken);
    }
}
