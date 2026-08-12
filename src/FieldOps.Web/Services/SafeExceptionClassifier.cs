using System.Data.Common;

using FieldOps.Domain.Common;

using Microsoft.EntityFrameworkCore;

namespace FieldOps.Web.Services;

internal static class SafeExceptionClassifier
{
    internal static SafeExceptionClassification Classify(Exception exception)
    {
        if (Contains<DbException>(exception) && Contains<TimeoutException>(exception))
        {
            return new(
                StatusCodes.Status503ServiceUnavailable,
                "database_timeout",
                nameof(DbException));
        }

        if (Contains<DbException>(exception))
        {
            return new(
                StatusCodes.Status503ServiceUnavailable,
                "database_unavailable",
                nameof(DbException));
        }

        return exception switch
        {
            DomainException => new(StatusCodes.Status400BadRequest, "domain", nameof(DomainException)),
            DbUpdateConcurrencyException => new(
                StatusCodes.Status409Conflict,
                "concurrency",
                nameof(DbUpdateConcurrencyException)),
            UnauthorizedAccessException => new(
                StatusCodes.Status403Forbidden,
                "authorization",
                nameof(UnauthorizedAccessException)),
            KeyNotFoundException => new(StatusCodes.Status404NotFound, "not_found", nameof(KeyNotFoundException)),
            _ => new(StatusCodes.Status500InternalServerError, "unexpected", "UnhandledException")
        };
    }

    private static bool Contains<TException>(Exception exception)
        where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed record SafeExceptionClassification(int StatusCode, string Category, string SafeType);