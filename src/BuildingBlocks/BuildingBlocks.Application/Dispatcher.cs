using BuildingBlocks.Domain;

using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Application;

/// <summary>Sends a command or a query to its one handler, after its validators.</summary>
public interface IDispatcher
{
    Task<Result<TResult>> Send<TCommand, TResult>(TCommand command, CancellationToken cancellationToken)
        where TCommand : ICommand<TResult>;

    Task<Result<TResult>> Query<TQuery, TResult>(TQuery query, CancellationToken cancellationToken)
        where TQuery : IQuery<TResult>;
}

/// <summary>
/// The whole dispatcher: find the validators and the handler in the container, run the validators,
/// stop on any error, else run the handler. Both type arguments are given at the call site, so no
/// reflection is needed.
/// </summary>
public sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    public Task<Result<TResult>> Send<TCommand, TResult>(TCommand command, CancellationToken cancellationToken)
        where TCommand : ICommand<TResult>
    {
        var errors = Validate(command);
        return errors.Count > 0
            ? Task.FromResult(Result.Failure<TResult>(errors))
            : services.GetRequiredService<ICommandHandler<TCommand, TResult>>().Handle(command, cancellationToken);
    }

    public Task<Result<TResult>> Query<TQuery, TResult>(TQuery query, CancellationToken cancellationToken)
        where TQuery : IQuery<TResult>
    {
        var errors = Validate(query);
        return errors.Count > 0
            ? Task.FromResult(Result.Failure<TResult>(errors))
            : services.GetRequiredService<IQueryHandler<TQuery, TResult>>().Handle(query, cancellationToken);
    }

    private List<Error> Validate<TRequest>(TRequest request) =>
        services.GetServices<IValidator<TRequest>>().SelectMany(v => v.Validate(request)).ToList();
}

public static class DispatcherRegistration
{
    public static IServiceCollection AddDispatcher(this IServiceCollection services) =>
        services.AddScoped<IDispatcher, Dispatcher>();
}
