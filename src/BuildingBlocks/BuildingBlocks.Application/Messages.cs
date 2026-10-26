using BuildingBlocks.Domain;

namespace BuildingBlocks.Application;

/// <summary>A request to change something. <typeparamref name="TResult"/> is what a success returns.</summary>
#pragma warning disable CA1040 // The interfaces carry the result type; they need no members.
public interface ICommand<TResult>;

/// <summary>A request to read something, which changes nothing.</summary>
public interface IQuery<TResult>;
#pragma warning restore CA1040

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<Result<TResult>> Handle(TQuery query, CancellationToken cancellationToken);
}

/// <summary>Checks that a request is well formed before its handler runs. Business rules live in the domain.</summary>
public interface IValidator<in TRequest>
{
    IEnumerable<Error> Validate(TRequest request);
}
