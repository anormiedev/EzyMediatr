using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;

namespace EzyMediatr.Core.Internal;

internal abstract class NotificationHandlerWrapper
{
    public abstract Task Handle(INotification notification, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal sealed class NotificationHandlerWrapper<TNotification> : NotificationHandlerWrapper
    where TNotification : INotification
{
    public override async Task Handle(INotification notification, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var typedNotification = (TNotification)notification;
        var handlers = serviceProvider.GetServices<INotificationHandler<TNotification>>().ToList();

        foreach (var handler in handlers)
        {
            await handler.Handle(typedNotification, cancellationToken);
        }
    }
}

internal static class NotificationHandlerWrapperFactory
{
    private static readonly ConcurrentDictionary<Type, NotificationHandlerWrapper> Cache = new();

    public static NotificationHandlerWrapper Create(Type notificationType)
    {
        return Cache.GetOrAdd(notificationType, static type =>
        {
            var constructed = (NotificationHandlerWrapper)Activator.CreateInstance(
                typeof(NotificationHandlerWrapper<>).MakeGenericType(type))!;
            return constructed;
        });
    }
}
