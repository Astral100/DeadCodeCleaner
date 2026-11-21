namespace Fixture.Domain;

// Name-based stand-ins for MediatR (the analyzer matches by simple name, as it does for the real library).
public interface IRequest<TResponse> { }

public interface IRequestHandler<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    TResponse Handle(TRequest request);
}

public class Mediator
{
    public object? Send(object request) => null;
}
