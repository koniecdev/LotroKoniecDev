using FluentValidation;
using FluentValidation.Results;
using Mediator;

namespace LotroKoniecDev.Application.Behaviors;

internal sealed class ValidationPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> 
    where TRequest : IMessage
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationPipelineBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }
    
    public async ValueTask<TResponse> Handle(
        TRequest message,
        MessageHandlerDelegate<TRequest, TResponse> next,
        CancellationToken cancellationToken)
    {
        if (_validators.Any())
        {
            ValidationContext<TRequest> context = new(message);

            ValidationResult[] validationFailures = await Task.WhenAll(
                _validators.Select(validator => validator.ValidateAsync(context, cancellationToken)));

            List<ValidationFailure> errors = validationFailures
                .Where(validationResult => !validationResult.IsValid)
                .SelectMany(validationResult => validationResult.Errors)
                .ToList();
        
            if (errors.Count != 0)
            {
                throw new ValidationException(errors);
            }
        }

        TResponse response = await next(message, cancellationToken);

        return response;
    }
}
