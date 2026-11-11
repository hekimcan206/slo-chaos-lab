using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace SloChaosLab.Api.Infrastructure;

/// <summary>
/// Turns database unavailability into 503 problem details with a stable code (and Retry-After while the breaker is
/// open), instead of a generic 500. Both count against the availability SLO; the difference is for callers, who
/// can tell "try again shortly" from "this is a bug". Anything else falls through to the default 500 handler.
/// </summary>
internal sealed partial class DependencyExceptionHandler(
    IProblemDetailsService problemDetails, IOptions<ResilienceOptions> options, ILogger<DependencyExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (httpContext.RequestAborted.IsCancellationRequested)
        {
            return false; // the client is gone; ASP.NET Core records 499
        }

        var (code, detail) = exception switch
        {
            BrokenCircuitException => ("dependency.circuit_open", "The ledger database is failing; requests are paused briefly. Retry with the same Idempotency-Key."),
            TimeoutRejectedException => ("dependency.timeout", "The ledger database did not answer within the request deadline. Retry with the same Idempotency-Key."),
            NpgsqlException or TimeoutException => ("dependency.unavailable", "The ledger database is unavailable. Retry with the same Idempotency-Key."),
            _ => (null, null),
        };

        if (code is null)
        {
            return false;
        }

        LogDependencyFailure(code, exception.GetType().Name);
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        if (exception is BrokenCircuitException)
        {
            httpContext.Response.Headers.RetryAfter = options.Value.BreakerBreakSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "A dependency is unavailable.",
                Detail = detail,
                Extensions = { ["code"] = code },
            },
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Posting failed on the database: {Code} ({ExceptionType})")]
    private partial void LogDependencyFailure(string code, string exceptionType);
}
