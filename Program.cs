using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryWidget;

static class Program
{
    [MTAThread]
    static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // `MemoryWidget.exe --dump <file>` writes the card templates and live data, for previewing the cards outside the Widgets board.
        if (args.Length == 2 && args[0] == "--dump")
        {
            var snapshot = MemorySnapshot.Capture(AdapterLimits.Query(), includeTopApps: true);
            var settings = WidgetSettings.Default;
            var dump = new JsonObject
            {
                ["template"] = JsonNode.Parse(CardTemplates.Memory),
                ["small"] = snapshot.ToCardData("small", settings),
                ["medium"] = snapshot.ToCardData("medium", settings),
                ["large"] = snapshot.ToCardData("large", settings),
                ["settingsTemplate"] = JsonNode.Parse(CardTemplates.Settings),
                ["settingsSmall"] = snapshot.ToSettingsData("small", settings, ""),
                ["settingsMedium"] = snapshot.ToSettingsData("medium", settings, ""),
            };
            File.WriteAllText(args[1], dump.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        // The Widgets board launches us with this argument (see Package.appxmanifest) and talks to us over COM.
        if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
        {
            ClassObject.Register(typeof(WidgetProvider).GUID, new WidgetProviderFactory(), out uint cookie);
            WidgetProvider.EmptyWidgetListEvent.WaitOne();
            ClassObject.Revoke(cookie);
        }

        return 0;
    }
}
