namespace EzyMediatr.Core.Abstractions;

/// <summary>
/// Marker interface for all mediator messages.
/// </summary>
public interface IBaseRequest;

public interface ITransactionalRequest : IBaseRequest;
