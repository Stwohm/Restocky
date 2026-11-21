using System;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace Restocker.Bridge;

internal sealed class GilhoundBridge : IDisposable
{
    private readonly TravelCommands commands = new(TimeProvider.System);
    private readonly BridgeFiles files;
    private static readonly string Version = typeof(Plugin).Assembly.GetName().Version!.ToString();
    private Task<InboxEntry?>? read;
    private Task? write;
    private TravelReply? pending;
    private Task? heartbeat;
    private long nextPoll;
    private long nextHeartbeat;

    internal GilhoundBridge(string configDirectory)
    {
        files = new(Path.Combine(configDirectory, "bridge"));
        Plugin.Framework.Update += OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        var tick = Environment.TickCount64;
        if (tick < nextPoll) return;
        nextPoll = tick + 250;

        if (tick >= nextHeartbeat && heartbeat is not { IsCompleted: false })
        {
            Observe(heartbeat);
            nextHeartbeat = tick + 5000;
            var now = DateTimeOffset.UtcNow;
            var loggedIn = Plugin.ClientState.IsLoggedIn;
            var world = loggedIn ? Plugin.ObjectTable.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ExtractText() : null;
            var state = new Heartbeat(1, Version, now.ToUnixTimeSeconds(), loggedIn, world ?? "", Plugin.Configuration.AllowGilhoundTravelCommands);
            heartbeat = Task.Run(() => files.UpdateHeartbeat(state, now));
        }

        if (write is { IsCompleted: false } || read is { IsCompleted: false }) return;
        if (write != null)
        {
            if (Observe(write)) pending = null;
            write = null;
        }
        if (read != null)
        {
            var entry = Observe(read) ? read.Result : null;
            read = null;
            if (entry != null)
            {
                try { pending = commands.Handle(entry, BlockedReason(), Dispatch); }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, "Gilhound command failed");
                    pending = new(1, entry.Id, "error", "command failed", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }
            }
        }
        if (pending is { } reply) write = Task.Run(() => files.Finish(reply));
        else read = Task.Run(() => files.ReadOldest(DateTimeOffset.UtcNow));
    }

    private static string? BlockedReason()
    {
        if (!Plugin.Configuration.AllowGilhoundTravelCommands) return "disabled";
        if (!Plugin.ClientState.IsLoggedIn) return "not logged in";
        var c = Plugin.Condition;
        if (c[ConditionFlag.InCombat]) return "in combat";
        if (c[ConditionFlag.BoundByDuty] || c[ConditionFlag.BoundByDuty56] || c[ConditionFlag.BoundByDuty95]) return "in a duty";
        if (c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78] || c[ConditionFlag.OccupiedInCutSceneEvent]) return "cutscene";
        if (c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51]) return "between areas";
        return null;
    }

    private static bool Dispatch(string command)
    {
        if (!Plugin.CommandManager.ProcessCommand(command)) return false;
        Plugin.ChatPrint($"Gilhound: {command}");
        return true;
    }

    private static bool Observe(Task? task)
    {
        try { task?.GetAwaiter().GetResult(); return true; }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Gilhound bridge file operation failed"); return false; }
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Observe(read);
        Observe(write);
        Observe(heartbeat);
    }
}
