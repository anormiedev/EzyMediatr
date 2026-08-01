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

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = RequestHandlerWrapperFactory<TResponse>.Create(request.GetType());
        return wrapper.Handle(request, serviceProvider, options, cancellationToken);
    }

    public IAsyncEnumerable<TResponse> Stream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = StreamRequestHandlerWrapperFactory<TResponse>.Create(request.GetType());
        return wrapper.Handle(request, serviceProvider, options, cancellationToken);
    }

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);

        var notificationType = notification.GetType();
        var wrapper = notificationType == typeof(TNotification)
            ? NotificationHandlerWrapperCache<TNotification>.Instance
            : NotificationHandlerWrapperFactory.Create(notificationType);
        return wrapper.Handle(notification, serviceProvider, options, cancellationToken);
    }
}
