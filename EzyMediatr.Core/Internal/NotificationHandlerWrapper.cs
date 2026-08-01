using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;
using EzyMediatr.Core.Pipeline;

namespace EzyMediatr.Core.Internal;

internal abstract class NotificationHandlerWrapper
{
    public abstract Task Handle(INotification notification, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal sealed class NotificationHandlerWrapper<TNotification> : NotificationHandlerWrapper
    where TNotification : INotification
{
    public override Task Handle(INotification notification, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var typedNotification = (TNotification)notification;

        Task ExecuteHandlers()
        {
            var registeredHandlers = serviceProvider.GetServices<INotificationHandler<TNotification>>();
            var handlers = registeredHandlers as INotificationHandler<TNotification>[]
                ?? registeredHandlers.ToArray();

            return handlers.Length switch
            {
                0 => Task.CompletedTask,
                1 => handlers[0].Handle(typedNotification, cancellationToken),
                _ => ExecuteSequentially(handlers)
            };
        }

        async Task ExecuteSequentially(INotificationHandler<TNotification>[] handlers)
        {
            foreach (var handler in handlers)
            {
                await handler.Handle(typedNotification, cancellationToken).ConfigureAwait(false);
            }
        }

        var registeredBehaviors = serviceProvider.GetServices<INotificationPipelineBehavior<TNotification>>();
        var behaviors = registeredBehaviors as INotificationPipelineBehavior<TNotification>[]
            ?? registeredBehaviors.ToArray();

        if (behaviors.Length == 0)
        {
            return ExecuteHandlers();
        }

        NotificationHandlerDelegate handlerDelegate = ExecuteHandlers;

        for (var index = behaviors.Length - 1; index >= 0; index--)
        {
            var behavior = behaviors[index];
            var next = handlerDelegate;
            handlerDelegate = () => behavior.Handle(typedNotification, next, cancellationToken);
        }

        return handlerDelegate();
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
