using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;
using EzyMediatr.Core.Pipeline;

namespace EzyMediatr.Core.Internal;

internal abstract class NotificationHandlerWrapper
{
    public abstract Task Handle(
        INotification notification,
        IServiceProvider serviceProvider,
        EzyMediatrOptions options,
        CancellationToken cancellationToken);
}

internal sealed class NotificationHandlerWrapper<TNotification> : NotificationHandlerWrapper
    where TNotification : INotification
{
    public override Task Handle(
        INotification notification,
        IServiceProvider serviceProvider,
        EzyMediatrOptions options,
        CancellationToken cancellationToken)
    {
        var typedNotification = (TNotification)notification;

        if ((options.GetPipelineFeatures<TNotification>() & PipelineFeatures.NotificationBehavior) == 0)
        {
            return ExecuteHandlers(typedNotification, serviceProvider, cancellationToken);
        }

        return ExecuteWithBehaviors(typedNotification, serviceProvider, cancellationToken);
    }

    private static Task ExecuteWithBehaviors(
        TNotification notification,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var registeredBehaviors = serviceProvider.GetServices<INotificationPipelineBehavior<TNotification>>();
        var behaviors = registeredBehaviors as INotificationPipelineBehavior<TNotification>[]
            ?? registeredBehaviors.ToArray();

        if (behaviors.Length == 0)
        {
            return ExecuteHandlers(notification, serviceProvider, cancellationToken);
        }

        NotificationHandlerDelegate handlerDelegate = () =>
            ExecuteHandlers(notification, serviceProvider, cancellationToken);

        if (behaviors.Length == 1)
        {
            return behaviors[0].Handle(notification, handlerDelegate, cancellationToken);
        }

        for (var index = behaviors.Length - 1; index >= 0; index--)
        {
            var behavior = behaviors[index];
            var next = handlerDelegate;
            handlerDelegate = () => behavior.Handle(notification, next, cancellationToken);
        }

        return handlerDelegate();
    }

    private static Task ExecuteHandlers(
        TNotification notification,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var registeredHandlers = serviceProvider.GetServices<INotificationHandler<TNotification>>();
        var handlers = registeredHandlers as INotificationHandler<TNotification>[]
            ?? registeredHandlers.ToArray();

        return handlers.Length switch
        {
            0 => Task.CompletedTask,
            1 => handlers[0].Handle(notification, cancellationToken),
            _ => ExecuteSequentially(notification, handlers, cancellationToken)
        };
    }

    private static async Task ExecuteSequentially(
        TNotification notification,
        INotificationHandler<TNotification>[] handlers,
        CancellationToken cancellationToken)
    {
        foreach (var handler in handlers)
        {
            await handler.Handle(notification, cancellationToken).ConfigureAwait(false);
        }
    }
}

internal static class NotificationHandlerWrapperCache<TNotification>
    where TNotification : INotification
{
    public static readonly NotificationHandlerWrapper Instance = new NotificationHandlerWrapper<TNotification>();
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
