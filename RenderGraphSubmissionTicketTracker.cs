namespace ArisenEngine.Rendering;

internal static class RenderGraphSubmissionTicketTracker
{
    public static bool CommitAcceptedTicket(
        ref ulong lastSubmittedTicket,
        ulong ticketBeforeExecution,
        ulong ticketAfterExecution)
    {
        if (ticketAfterExecution == 0 ||
            ticketAfterExecution <= ticketBeforeExecution)
        {
            return false;
        }

        lastSubmittedTicket = Math.Max(
            lastSubmittedTicket,
            ticketAfterExecution);
        return true;
    }
}
