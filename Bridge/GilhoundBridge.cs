using System;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using Restocker.Common;

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
    private nint openedBoard;
    private long nextPoll;
    private long nextHeartbeat;

    internal GilhoundBridge(string configDirectory)
    {
        files = new(Path.Combine(configDirectory, "bridge"));
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ItemSearch", OnBoardOpened);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "ItemSearch", OnBoardOpened);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ItemSearch", OnBoardClosed);
        Plugin.Framework.Update += OnUpdate;
    }

    private static string? CurrentWorld => Plugin.ObjectTable.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ExtractText();

    private void OnBoardOpened(AddonEvent type, AddonArgs args)
    {
        if (commands.HasSearch && commands.AwaitingBoard(CurrentWorld))
            openedBoard = args.Addon.Address;
    }

    private void OnBoardClosed(AddonEvent type, AddonArgs args)
    {
        if (openedBoard != args.Addon.Address) return;
        openedBoard = 0;
        commands.ClearSearch();
    }

    private unsafe void SearchOpenedBoard()
    {
        var address = openedBoard;
        openedBoard = 0;
        var board = (AddonItemSearch*)AddonHelper.GetVisible("ItemSearch");
        if (board == null || (nint)board != address)
        {
            commands.ClearSearch();
            return;
        }
        if (board->SearchTextInput == null)
        {
            if (commands.AwaitingBoard(CurrentWorld)) openedBoard = address;
            return;
        }

        var name = commands.TakeSearchName(CurrentWorld,
            id => Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(id, out var row) ? row.Name.ExtractText() : null,
            id => Plugin.Log.Warning($"Gilhound: no localized Item name for id {id}"));
        if (name == null) return;

        // Independent implementation of the HaselTweaks / HaselCommon SearchForItem approach.
        var length = Math.Min(name.Length, 40);
        if (char.IsHighSurrogate(name[length - 1])) length--;
        board->SearchTextInput->SetText(name[..length]);
        board->SetModeFilter(AddonItemSearch.SearchMode.Normal, -1);
        board->RunSearch(false);
        Plugin.ChatPrint($"Gilhound: searched {name} on the market board");
    }

    private void OnUpdate(IFramework framework)
    {
        if (commands.HasSearch && (!Plugin.Configuration.SearchGilhoundItem || !Plugin.Configuration.AllowGilhoundTravelCommands))
        {
            commands.ClearSearch();
            openedBoard = 0;
        }
        if (openedBoard != 0) SearchOpenedBoard();

        var tick = Environment.TickCount64;
        if (tick < nextPoll) return;
        nextPoll = tick + 250;

        if (tick >= nextHeartbeat && heartbeat is not { IsCompleted: false })
        {
            Observe(heartbeat);
            nextHeartbeat = tick + 5000;
            var now = DateTimeOffset.UtcNow;
            var loggedIn = Plugin.ClientState.IsLoggedIn;
            var world = loggedIn ? CurrentWorld : null;
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
                try
                {
                    pending = commands.Handle(entry, BlockedReason(), Dispatch, Plugin.Configuration.SearchGilhoundItem);
                    if (pending.Status == "ok") openedBoard = 0;
                }
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
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ItemSearch", OnBoardOpened);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "ItemSearch", OnBoardOpened);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "ItemSearch", OnBoardClosed);
        Plugin.Framework.Update -= OnUpdate;
        Observe(read);
        Observe(write);
        Observe(heartbeat);
    }
}
