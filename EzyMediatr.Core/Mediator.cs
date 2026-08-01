using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Internal;

[assembly: InternalsVisibleTo("EzyMediatr.DependencyInjection")]

namespace EzyMediatr.Core;

public class Mediator(IServiceProvider serviceProvider, EzyMediatrOptions options) : IMediator
{
    public Mediator(IServiceProvider serviceProvider)
        : this(serviceProvider, serviceProvider.GetService<EzyMediatrOptions>() ?? new EzyMediatrOptions())
    {
    }

    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = RequestHandlerWrapperFactory<TResponse>.Create(request.GetType());
        return await wrapper.Handle(request, serviceProvider, options, cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<TResponse> Stream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = StreamRequestHandlerWrapperFactory<TResponse>.Create(request.GetType());
        return wrapper.Handle(request, serviceProvider, cancellationToken);
    }

    public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);

        var wrapper = NotificationHandlerWrapperFactory.Create(notification.GetType());
        await wrapper.Handle(notification, serviceProvider, cancellationToken);
    }
}
