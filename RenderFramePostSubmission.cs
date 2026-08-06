using System.Runtime.ExceptionServices;

namespace ArisenEngine.Rendering;

internal interface IRenderFramePostSubmissionActions
{
    void NotifyFrameSubmitted(ulong submittedTicket);

    void CompleteReadback(ulong submittedTicket);

    void AbortReadback(Exception executionFailure);
}

internal static class RenderFramePostSubmission
{
    public static void Execute<TActions>(
        ref TActions actions,
        ulong submittedTicket)
        where TActions : struct, IRenderFramePostSubmissionActions
    {
        Exception? failure = null;
        try
        {
            actions.NotifyFrameSubmitted(submittedTicket);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            actions.CompleteReadback(submittedTicket);
        }
        catch (Exception ex)
        {
            failure = failure == null
                ? ex
                : RenderFrameFailureAggregator.Append(
                    failure,
                    "visual readback completion",
                    ex);
        }

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public static void ThrowExecutionFailure<TActions>(
        ref TActions actions,
        ulong ticketBeforeExecution,
        ulong acceptedTicket,
        Exception executionFailure)
        where TActions : struct, IRenderFramePostSubmissionActions
    {
        ArgumentNullException.ThrowIfNull(executionFailure);
        Exception? notificationFailure = null;
        Exception? readbackAbortFailure = null;

        if (acceptedTicket > ticketBeforeExecution)
        {
            try
            {
                actions.NotifyFrameSubmitted(acceptedTicket);
            }
            catch (Exception ex)
            {
                notificationFailure = ex;
            }
        }

        try
        {
            actions.AbortReadback(executionFailure);
        }
        catch (Exception ex)
        {
            readbackAbortFailure = ex;
        }

        if (notificationFailure == null && readbackAbortFailure == null)
        {
            ExceptionDispatchInfo.Capture(executionFailure).Throw();
        }

        var failures = new List<Exception>(3)
        {
            executionFailure
        };
        if (notificationFailure != null)
        {
            failures.Add(new InvalidOperationException(
                "Render frame submission notification failed.",
                notificationFailure));
        }
        if (readbackAbortFailure != null)
        {
            failures.Add(new InvalidOperationException(
                "Render frame visual readback cancellation failed.",
                readbackAbortFailure));
        }

        throw new AggregateException(
            "Render graph execution and exceptional post-submission handling failed.",
            failures);
    }
}
