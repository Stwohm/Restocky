using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using Item = Lumina.Excel.Sheets.Item;
using World = Lumina.Excel.Sheets.World;
using Restocker.Common;

namespace Restocker.Bridge;

internal sealed class GilhoundBridge : IDisposable
{
    private readonly TravelCommands commands = new(TimeProvider.System);
    private readonly BridgeFiles files;
    private static readonly string Version = typeof(Plugin).Assembly.GetName().Version!.ToString();
    private readonly BridgePump pump;
    private nint openedBoard;
    // Built by the inbox worker before handing off the first item request.
    private Dictionary<uint, string>? worldNames;
    private string? searchName;

    internal GilhoundBridge(string configDirectory)
    {
        files = new(Path.Combine(configDirectory, "bridge"));
        pump = new(TimeProvider.System, Task.Run, ReadEntry, Handle, files.Finish, CaptureHeartbeat, ReportError);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ItemSearch", OnBoardOpened);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "ItemSearch", OnBoardOpened);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ItemSearch", OnBoardClosed);
        Plugin.Framework.Update += OnUpdate;
    }

    private static uint CurrentWorldId => Plugin.ObjectTable.LocalPlayer?.CurrentWorld.RowId ?? 0;
    private string? CurrentWorld => worldNames?.GetValueOrDefault(CurrentWorldId);

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
            _ => searchName,
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
        var start = Stopwatch.GetTimestamp();
        if (commands.HasSearch && (!Plugin.Configuration.SearchGilhoundItem || !Plugin.Configuration.AllowGilhoundTravelCommands))
        {
            commands.ClearSearch();
            openedBoard = 0;
        }
        if (openedBoard != 0) SearchOpenedBoard();

        var afterSearch = Stopwatch.GetTimestamp();
        pump.TickHeartbeat();
        var afterHeartbeat = Stopwatch.GetTimestamp();
        pump.TickMailbox();
        var end = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(start, end);
        if (elapsed.TotalMilliseconds > 5)
        {
            var search = afterSearch - start;
            var beat = afterHeartbeat - afterSearch;
            var inbox = end - afterHeartbeat;
            var phase = inbox >= Math.Max(search, beat) ? "inbox/dispatch" : beat >= search ? "heartbeat" : "board search";
            Plugin.Log.Debug($"Gilhound OnUpdate took {elapsed.TotalMilliseconds:F2} ms (longest phase: {phase})");
        }
    }

    private InboxEntry? ReadEntry()
    {
        var entry = files.ReadOldest(DateTimeOffset.UtcNow);
        if (entry is not { ValidEnvelope: true, Destination: not null, Request.Item: { Id: > 0 } item }) return entry;
        try
        {
            worldNames ??= Plugin.DataManager.GetExcelSheet<World>()
                .ToDictionary(world => world.RowId, world => world.Name.ExtractText());
            var name = Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(item.Id, out var row)
                ? row.Name.ExtractText() : null;
            return entry with { LocalizedName = name };
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Gilhound item search data unavailable");
            return entry;
        }
    }

    private TravelReply Handle(InboxEntry entry)
    {
        try
        {
            var reply = commands.Handle(entry, BlockedReason(), Dispatch, Plugin.Configuration.SearchGilhoundItem);
            if (reply.Status == "ok")
            {
                openedBoard = 0;
                searchName = entry.LocalizedName;
            }
            return reply;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Gilhound command failed");
            return new(1, entry.Id, "error", "command failed", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
    }

    private Action CaptureHeartbeat()
    {
        var now = DateTimeOffset.UtcNow;
        var loggedIn = Plugin.ClientState.IsLoggedIn;
        var worldId = loggedIn ? CurrentWorldId : 0;
        var enabled = Plugin.Configuration.AllowGilhoundTravelCommands;
        return () =>
        {
            try
            {
                var world = worldId != 0 && Plugin.DataManager.GetExcelSheet<World>().TryGetRow(worldId, out var row)
                    ? row.Name.ExtractText() : "";
                files.UpdateHeartbeat(new(1, Version, now.ToUnixTimeSeconds(), loggedIn, world, enabled), now);
            }
            catch (Exception ex) { ReportError(ex); }
        };
    }

    private static void ReportError(Exception ex) => Plugin.Log.Warning(ex, "Gilhound bridge background operation failed");

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

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ItemSearch", OnBoardOpened);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "ItemSearch", OnBoardOpened);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "ItemSearch", OnBoardClosed);
        Plugin.Framework.Update -= OnUpdate;
        pump.Drain();
    }
}
