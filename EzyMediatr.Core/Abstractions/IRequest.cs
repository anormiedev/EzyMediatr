namespace EzyMediatr.Core.Abstractions;

public interface IRequest<out TResponse> : IBaseRequest;

public interface IStreamRequest<out TResponse> : IBaseRequest;

public interface INotification : IBaseRequest;
