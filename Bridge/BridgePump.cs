using System;
using System.Threading.Tasks;

namespace Restocker.Bridge;

// Only the framework thread advances the pump. The worker publishes the read
// payload through task completion, so the tick never needs a result accessor.
internal sealed class BridgePump(TimeProvider clock, Func<Action, Task> schedule,
    Func<InboxEntry?> read, Func<InboxEntry, TravelReply> handle, Action<TravelReply> write,
    Func<Action> captureHeartbeat, Action<Exception> reportError)
{
    private Task? mailbox;
    private Task? heartbeat;
    private InboxEntry? entry;
    private TravelReply? pending;
    private long? lastPoll;
    private long? lastHeartbeat;

    internal void TickHeartbeat()
    {
        var now = clock.GetTimestamp();
        if (lastHeartbeat is long last && clock.GetElapsedTime(last, now) < TimeSpan.FromSeconds(5)) return;
        if (heartbeat is { IsCompleted: false }) return;
        lastHeartbeat = now;
        heartbeat = schedule(captureHeartbeat());
    }

    internal void TickMailbox()
    {
        var now = clock.GetTimestamp();
        if (lastPoll is long last && clock.GetElapsedTime(last, now) < TimeSpan.FromMilliseconds(250)) return;
        if (mailbox is { IsCompleted: false }) return;
        lastPoll = now;

        if (entry is { } ready)
        {
            entry = null;
            pending = handle(ready);
        }

        mailbox = pending == null ? schedule(Read) : schedule(Write);
    }

    private void Read()
    {
        try { entry = read(); }
        catch (Exception ex) { reportError(ex); }
    }

    private void Write()
    {
        try
        {
            write(pending!);
            pending = null;
        }
        catch (Exception ex) { reportError(ex); }
    }

    // Unload only. Join workers before releasing plugin services or reloading the mailbox.
    internal void Drain()
    {
        mailbox?.GetAwaiter().GetResult();
        heartbeat?.GetAwaiter().GetResult();
    }
}
