using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Restocker.Execution;

namespace Restocker;

/// <summary>
/// Fork: thin AutoRetainer IPC client. Endpoint names checked against PunishXIV/AutoRetainer
/// master on 2026-11-20 (Modules/IPC.cs, Modules/EzIPCManagers/IPC_PluginState.cs).
/// Every call swallows failures: an AutoRetainer update must never break Restocky.
/// </summary>
public sealed class AutoRetainerIpc
{
    private readonly IDalamudPluginInterface pi;
    private readonly IPluginLog log;
    private readonly Func<bool> isLoaded;
    private bool suppressedByUs;
    private bool releasePending;
    private long retryReleaseAt;
    private readonly AutoRetainerPostprocess postprocess = new();
    private readonly ICallGateSubscriber<bool> busy;
    private readonly string pluginName;
    private readonly ICallGateSubscriber<string, object> postprocessOffer;
    private readonly ICallGateSubscriber<string, string, object> postprocessGrant;
    private readonly ICallGateSubscriber<string, object> postprocessRequest;
    private readonly ICallGateSubscriber<object> postprocessFinish;
    private Func<bool>? shouldRequestPostprocess;
    private Func<string, bool>? startPostprocess;
    private Action? interruptPostprocess;

    public AutoRetainerIpc(IDalamudPluginInterface pi, IPluginLog log, Func<bool> isLoaded)
    {
        this.pi = pi;
        this.log = log;
        this.isLoaded = isLoaded;
        pluginName = pi.InternalName;
        busy = pi.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy");
        postprocessOffer = pi.GetIpcSubscriber<string, object>("AutoRetainer.OnRetainerAdditionalTask");
        postprocessGrant = pi.GetIpcSubscriber<string, string, object>("AutoRetainer.OnRetainerReadyForPostprocess");
        postprocessRequest = pi.GetIpcSubscriber<string, object>("AutoRetainer.RequestPostprocess");
        postprocessFinish = pi.GetIpcSubscriber<object>("AutoRetainer.FinishPostprocessRequest");
    }

    public bool IsLoaded => isLoaded();

    internal bool HasPostprocess => postprocess.Active;
    internal bool ReleasePending => releasePending || postprocess.FinishPending;
    internal bool PostprocessTimedOut => postprocess.TimedOut(Environment.TickCount64);

    internal bool PostprocessInterrupted()
    {
        if (!IsLoaded) return true;
        try { return !busy.InvokeFunc(); }
        catch (Exception ex)
        {
            log.Debug(ex, "[Restocker] AutoRetainer busy state unavailable during postprocess");
            return false;
        }
    }

    internal void ListenForPostprocess(Func<bool> shouldRequest, Func<string, bool> start, Action interrupt)
    {
        shouldRequestPostprocess = shouldRequest;
        startPostprocess = start;
        interruptPostprocess = interrupt;
        postprocessGrant.Subscribe(OnPostprocessGrant);
        postprocessOffer.Subscribe(OnPostprocessOffer);
    }

    internal void StopListeningForPostprocess()
    {
        shouldRequestPostprocess = null;
        startPostprocess = null;
        interruptPostprocess = null;
        FinishPostprocess();
        if (postprocess.FinishPending) FinishPostprocess(); // last chance without the framework retry tick
        // AR has no withdraw IPC. If it already accepted a request, keep answering until
        // that grant is released or the next offer replaces the old request list.
        if (!postprocess.Pending || PostprocessInterrupted()) UnsubscribePostprocess();
    }

    private void UnsubscribePostprocess()
    {
        postprocessOffer.Unsubscribe(OnPostprocessOffer);
        postprocessGrant.Unsubscribe(OnPostprocessGrant);
    }

    private void OnPostprocessOffer(string _)
    {
        try
        {
            SupersedePostprocess();
            if (postprocess.OnOffer(shouldRequestPostprocess?.Invoke() == true))
                postprocessRequest.InvokeAction(pluginName);
            else if (shouldRequestPostprocess == null)
                UnsubscribePostprocess();
        }
        catch (Exception ex)
        {
            postprocess.OnOffer(false);
            log.Warning(ex, "[Restocker] AutoRetainer postprocess request failed");
        }
    }

    private void OnPostprocessGrant(string owner, string retainerName)
    {
        if (owner != pluginName) SupersedePostprocess();
        if (!postprocess.OnGrant(owner == pluginName, Environment.TickCount64)) return;
        var started = false;
        try { started = startPostprocess?.Invoke(retainerName) == true; }
        catch (Exception ex) { log.Warning(ex, "[Restocker] AutoRetainer postprocess start failed"); }
        finally
        {
            if (!started) FinishPostprocess();
            if (startPostprocess == null)
            {
                if (postprocess.FinishPending) FinishPostprocess();
                UnsubscribePostprocess();
            }
        }
    }

    internal void FinishPostprocess()
    {
        try { postprocess.Finish(() => postprocessFinish.InvokeAction()); }
        catch (Exception ex) { log.Warning(ex, "[Restocker] AutoRetainer postprocess finish failed"); }
    }

    private void SupersedePostprocess()
    {
        postprocess.ForgetFinish();
        if (postprocess.TakeFinish()) interruptPostprocess?.Invoke();
    }

    internal void RetryRelease()
    {
        if (!ReleasePending || Environment.TickCount64 < retryReleaseAt) return;
        retryReleaseAt = Environment.TickCount64 + 1000;
        if (!IsLoaded)
        {
            postprocess.ForgetFinish();
            suppressedByUs = releasePending = false;
            return;
        }
        if (postprocess.FinishPending) FinishPostprocess();
        if (releasePending) Release();
    }

    /// <summary>Null when AutoRetainer is not loaded or the IPC answer could not be read.</summary>
    public AutoRetainerState? Read(ulong contentId)
    {
        if (!isLoaded()) return null;
        try
        {
            var isBusy = busy.InvokeFunc();
            long? secs = null;
            try
            {
                secs = pi.GetIpcSubscriber<ulong, long?>("AutoRetainer.PluginState.GetClosestRetainerVentureSecondsRemaining")
                    .InvokeFunc(contentId);
            }
            catch (Exception ex) { log.Debug(ex, "[Restocker] AR venture seconds unavailable"); }
            var sense = Try(() => pi.GetIpcSubscriber<bool>("AutoRetainer.PluginState.GetOptionRetainerSense").InvokeFunc());
            var multi = Try(() => pi.GetIpcSubscriber<bool>("AutoRetainer.GetMultiModeEnabled").InvokeFunc());
            return new AutoRetainerState(isBusy, secs, sense, multi);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Restocker] AutoRetainer IPC read failed");
            return null;
        }
    }

    /// <summary>Pause AutoRetainer's scheduler / multi mode for the length of our cycle.</summary>
    public bool Suppress()
    {
        if (!isLoaded() || suppressedByUs) return true;
        try
        {
            var already = pi.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed").InvokeFunc();
            if (already) return true; // someone else suppressed it; leave their state alone
            pi.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(true);
            suppressedByUs = true;
            log.Information("[Restocker] AutoRetainer suppressed for the cycle");
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Restocker] AutoRetainer suppress failed");
            return false;
        }
    }

    /// <summary>Undo <see cref="Suppress"/>. Safe to call repeatedly.</summary>
    public void Release()
    {
        if (!suppressedByUs) return;
        try
        {
            pi.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(false);
            suppressedByUs = releasePending = false;
            log.Information("[Restocker] AutoRetainer released");
        }
        catch (Exception ex)
        {
            releasePending = true;
            log.Warning(ex, "[Restocker] AutoRetainer release failed");
        }
    }

    private static bool Try(Func<bool> f)
    {
        try { return f(); } catch { return false; }
    }
}
