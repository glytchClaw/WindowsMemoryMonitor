using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;

namespace MemoryWidget;

internal static class CardTemplates
{
    public static readonly string Memory = Read("MemoryWidget.template.json");
    public static readonly string Settings = Read("MemoryWidget.settings.json");

    static string Read(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Serves the Memory Monitor card to the Widgets board. The board calls Activate while the widget is visible
/// and Deactivate when it's hidden, so we only poll memory while someone can see it. "..." > Customize widget
/// calls OnCustomizationRequested, which swaps in the settings card until the user saves or cancels.
/// </summary>
[ComVisible(true)]
[ComDefaultInterface(typeof(IWidgetProvider))]
[Guid("8E3B6F5A-2C41-4D7E-9B6A-5F1C3A7D9E21")]
internal sealed class WidgetProvider : IWidgetProvider, IWidgetProvider2
{
    public static readonly ManualResetEvent EmptyWidgetListEvent = new(false);

    static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);
    static readonly TimeSpan AdapterLimitsMaxAge = TimeSpan.FromMinutes(1);

    sealed class WidgetState
    {
        public WidgetSize Size;
        public bool IsActive;
        public WidgetSettings Settings = WidgetSettings.Default;
        public bool InCustomization;
        public string Error = "";
    }

    sealed record PendingUpdate(string Id, WidgetSize Size, WidgetSettings Settings, bool InCustomization, string Error);

    static readonly object Gate = new();
    static readonly Dictionary<string, WidgetState> Widgets = new();
    static readonly Timer RefreshTimer = new(_ => RefreshActiveWidgets());
    static IReadOnlyList<AdapterLimit>? adapterLimits;
    static DateTime adapterLimitsReadAt;

    public WidgetProvider()
    {
        // When the board relaunches us, pick up the widgets that are already pinned, with their saved settings.
        foreach (var info in WidgetManager.GetDefault().GetWidgetInfos())
        {
            var context = info.WidgetContext;
            var state = new WidgetState { Size = context.Size, IsActive = context.IsActive, Settings = WidgetSettings.FromJson(info.CustomState) };
            lock (Gate) Widgets.TryAdd(context.Id, state);
        }
        UpdateTimer();
    }

    public void CreateWidget(WidgetContext widgetContext)
    {
        lock (Gate)
        {
            var state = Track(widgetContext);
            state.IsActive = widgetContext.IsActive;
        }
        Push([widgetContext.Id]);
        UpdateTimer();
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        lock (Gate)
        {
            Widgets.Remove(widgetId);
            if (Widgets.Count == 0) EmptyWidgetListEvent.Set();
        }
        UpdateTimer();
    }

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        string id = actionInvokedArgs.WidgetContext.Id;
        switch (actionInvokedArgs.Verb)
        {
            case "openIntel":
                OpenGraphicsSettings();
                break;

            case "saveSettings":
                lock (Gate)
                {
                    var state = Track(actionInvokedArgs.WidgetContext);
                    if (WidgetSettings.TryParseTarget(actionInvokedArgs.Data, out double gb))
                    {
                        state.Settings = state.Settings with { TargetGb = gb };
                        state.InCustomization = false;
                        state.Error = "";
                    }
                    else
                    {
                        state.Error = $"Enter a size between {WidgetSettings.MinTargetGb} and {WidgetSettings.MaxTargetGb} GB, like 5 or 4.5.";
                    }
                }
                break;

            case "cancelSettings":
                lock (Gate)
                {
                    var state = Track(actionInvokedArgs.WidgetContext);
                    state.InCustomization = false;
                    state.Error = "";
                }
                break;
        }
        Push([id]);
    }

    public void OnCustomizationRequested(WidgetCustomizationRequestedArgs customizationRequestedArgs)
    {
        lock (Gate)
        {
            var state = Track(customizationRequestedArgs.WidgetContext);
            state.InCustomization = true;
            state.Error = "";
        }
        Push([customizationRequestedArgs.WidgetContext.Id]);
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        lock (Gate) Track(contextChangedArgs.WidgetContext);
        Push([contextChangedArgs.WidgetContext.Id]);
    }

    public void Activate(WidgetContext widgetContext)
    {
        lock (Gate)
        {
            Track(widgetContext).IsActive = true;
            adapterLimits = null; // re-check the limit every time the board opens
        }
        Push([widgetContext.Id]);
        UpdateTimer();
    }

    public void Deactivate(string widgetId)
    {
        lock (Gate)
        {
            if (Widgets.TryGetValue(widgetId, out var state)) state.IsActive = false;
        }
        UpdateTimer();
    }

    /// <summary>Returns the state for a widget, recovering its saved settings if we haven't seen it yet. Call under Gate.</summary>
    static WidgetState Track(WidgetContext context)
    {
        if (!Widgets.TryGetValue(context.Id, out var state))
        {
            string? customState = null;
            try
            {
                customState = WidgetManager.GetDefault().GetWidgetInfo(context.Id)?.CustomState;
            }
            catch (Exception)
            {
                // A brand-new widget has no saved state yet.
            }
            Widgets[context.Id] = state = new WidgetState { Settings = WidgetSettings.FromJson(customState) };
        }
        state.Size = context.Size;
        return state;
    }

    static void UpdateTimer()
    {
        bool anyActive;
        lock (Gate) anyActive = Widgets.Values.Any(w => w.IsActive);
        RefreshTimer.Change(anyActive ? RefreshInterval : Timeout.InfiniteTimeSpan, anyActive ? RefreshInterval : Timeout.InfiniteTimeSpan);
    }

    static void RefreshActiveWidgets()
    {
        // Leave widgets showing the settings card alone; re-sending it would wipe what the user is typing.
        List<string> active;
        lock (Gate) active = Widgets.Where(w => w.Value.IsActive && !w.Value.InCustomization).Select(w => w.Key).ToList();
        if (active.Count > 0) Push(active);
    }

    static void Push(IReadOnlyCollection<string> widgetIds)
    {
        try
        {
            List<PendingUpdate> updates;
            IReadOnlyList<AdapterLimit> limits;
            lock (Gate)
            {
                updates = widgetIds.Where(Widgets.ContainsKey)
                    .Select(id =>
                    {
                        var w = Widgets[id];
                        return new PendingUpdate(id, w.Size, w.Settings, w.InCustomization, w.Error);
                    })
                    .ToList();
                if (adapterLimits == null || DateTime.Now - adapterLimitsReadAt > AdapterLimitsMaxAge)
                {
                    adapterLimits = AdapterLimits.Query();
                    adapterLimitsReadAt = DateTime.Now;
                }
                limits = adapterLimits;
            }
            if (updates.Count == 0) return;

            var snapshot = MemorySnapshot.Capture(limits, includeTopApps: updates.Any(u => u.Size == WidgetSize.Large && !u.InCustomization));
            foreach (var u in updates)
            {
                string size = u.Size.ToString().ToLowerInvariant();
                var update = new WidgetUpdateRequestOptions(u.Id)
                {
                    Template = u.InCustomization ? CardTemplates.Settings : CardTemplates.Memory,
                    Data = (u.InCustomization ? snapshot.ToSettingsData(size, u.Settings, u.Error) : snapshot.ToCardData(size, u.Settings)).ToJsonString(),
                    CustomState = u.Settings.ToJson(),
                };
                try
                {
                    WidgetManager.GetDefault().UpdateWidget(update);
                }
                catch (Exception)
                {
                    // The widget may have been removed between the snapshot and the update.
                }
            }
        }
        catch (Exception)
        {
            // Never let a failed refresh take down the provider; the next tick will try again.
        }
    }

    static void OpenGraphicsSettings()
    {
        string intel = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel", "Intel Graphics Software", "IntelGraphicsSoftware.exe");
        string target = File.Exists(intel) ? intel : "ms-settings:display-advancedgraphics";
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Nothing else useful to do from a widget.
        }
    }
}
