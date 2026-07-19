namespace DeskPilot.Application.Voice;

/// <summary>Defines exceptions that must never be contained by voice-pipeline boundaries.</summary>
internal static class VoiceExceptionPolicy
{
    /// <summary>Returns whether an exception or one of its nested failures is process-fatal.</summary>
    public static bool IsFatal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OutOfMemoryException
            or AccessViolationException
            or AppDomainUnloadedException
            or BadImageFormatException
            or StackOverflowException)
        {
            return true;
        }

        if (exception is AggregateException aggregate
            && aggregate.InnerExceptions.Any(IsFatal))
        {
            return true;
        }

        return exception.InnerException is not null && IsFatal(exception.InnerException);
    }
}
