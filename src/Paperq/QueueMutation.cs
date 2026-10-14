using System.Diagnostics;

namespace Paperq;

internal static class QueueMutation
{
    internal static FileStream Enter(QueueLayout layout, bool recover = true)
    {
        layout.RequireInitialized();
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            FileStream stream;
            try
            {
                stream = TextFile.OpenExclusiveLock(Path.Combine(layout.QueueRoot, ".queue.lock"));
            }
            catch (IOException) when (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(20);
                continue;
            }
            catch (IOException exception)
            {
                throw new PaperqException("queue_busy", "Another paperq command owns the queue; retry the command.",
                    PaperqExitCode.Conflict, exception);
            }

            try
            {
                if (recover)
                {
                    QueueMaintenance.Recover(layout);
                }
                else
                {
                    QueueMaintenance.RequireNoPending(layout);
                }
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
    }
}
