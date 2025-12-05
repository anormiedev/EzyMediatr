namespace EzyMediatr.Core.Pipeline;

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<TResponse>();

public delegate Task NotificationHandlerDelegate();
