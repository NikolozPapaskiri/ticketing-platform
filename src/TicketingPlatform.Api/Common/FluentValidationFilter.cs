using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Diagnostics.Contracts;
using System.Net.Mime;

namespace TicketingPlatform.Api.Common;

/// <summary>
/// Runs FluentValidation on every action argument that has a registered IValidator&lt;T&gt;,
/// after model binding and before the action body. On failure it short-circuits with an
/// RFC 7807 ValidationProblem (400 + per-field errors), so controllers no longer repeat
/// validate-and-return blocks.
/// </summary>
public sealed class FluentValidationFilter : IAsyncActionFilter
{
    private readonly IServiceProvider _services;
    private readonly ProblemDetailsFactory _problemDetailsFactory;

    public FluentValidationFilter(IServiceProvider services, ProblemDetailsFactory problemDetailsFactory)
    {
        _services = services;
        _problemDetailsFactory = problemDetailsFactory;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null) continue; // Let [Required] handle this if needed

            // Resolve IValidator<TArg> at runtime. No validator registered => skip (e.g. Guid, int, CancellationToken).
            var validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());
            if (_services.GetService(validatorType) is not IValidator validator) continue;

            var result = await validator.ValidateAsync(new ValidationContext<object>(argument), context.HttpContext.RequestAborted);

            if (result.IsValid) continue;

            var modelState = new ModelStateDictionary();
            foreach (var error in result.Errors)
                modelState.AddModelError(error.PropertyName, error.ErrorMessage);

            // Go through ProblemDetailsFactory so the body carries type + traceId like every other error.
            var problem = _problemDetailsFactory.CreateValidationProblemDetails(context.HttpContext, modelState);
            context.Result = new ObjectResult(problem)
            {
                StatusCode = problem.Status,
                ContentTypes = { "application/problem+json" }
            };
            return; // short-circuit: the action body does not run
        }

        await next(); // valid (or nothing to validate): run the action
    }
}
