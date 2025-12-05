using EzyMediatr.Core.Abstractions;

namespace EzyMediatr.Core.Pipeline;

public interface IPipelineBehavior<in TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}

public interface IStreamPipelineBehavior<in TRequest, TResponse> where TRequest : IStreamRequest<TResponse>
{
    IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}

public interface IRequestPreProcessor<in TRequest> where TRequest : IBaseRequest
{
    Task Process(TRequest request, CancellationToken cancellationToken);
}

public interface IRequestPostProcessor<in TRequest, in TResponse> where TRequest : IBaseRequest
{
    Task Process(TRequest request, TResponse response, CancellationToken cancellationToken);
}
