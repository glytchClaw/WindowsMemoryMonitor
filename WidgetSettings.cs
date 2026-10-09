using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MemoryWidget;

/// <summary>Per-widget settings. The Widgets board stores them as the widget's custom state, so they survive restarts.</summary>
internal sealed record WidgetSettings(double TargetGb)
{
    public const double MinTargetGb = 0.25, MaxTargetGb = 256;
    public static readonly WidgetSettings Default = new(5);

    /// <summary>The GPU/NPU shared memory limit should stay at or below this.</summary>
    public ulong TargetBytes => (ulong)(TargetGb * (1UL << 30));

    public string TargetText => TargetGb.ToString("0.##", CultureInfo.CurrentCulture);

    public string ToJson() => new JsonObject { ["targetGb"] = TargetGb }.ToJsonString();

    public static WidgetSettings FromJson(string? customState)
    {
        if (string.IsNullOrEmpty(customState)) return Default;
        try
        {
            return JsonNode.Parse(customState)?["targetGb"] is JsonValue value && value.TryGetValue(out double gb) && IsValid(gb)
                ? new WidgetSettings(gb)
                : Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    /// <summary>Reads the target from the settings card's submitted inputs, e.g. {"targetGb":"4.5"}.</summary>
    public static bool TryParseTarget(string? actionData, out double gb)
    {
        gb = 0;
        if (string.IsNullOrEmpty(actionData)) return false;
        JsonNode? input;
        try
        {
            input = Find(JsonNode.Parse(actionData), "targetGb");
        }
        catch (JsonException)
        {
            return false;
        }

        bool parsed = input is JsonValue value && (value.TryGetValue(out gb) ||
            (value.TryGetValue(out string? text) &&
             (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out gb) ||
              double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out gb))));
        gb = Math.Round(gb, 2);
        return parsed && IsValid(gb);
    }

    static bool IsValid(double gb) => gb >= MinTargetGb && gb <= MaxTargetGb;

    // Input values may arrive at the top level or nested, depending on the host; look for the id anywhere.
    static JsonNode? Find(JsonNode? node, string name) => node switch
    {
        JsonObject obj => obj.TryGetPropertyValue(name, out var hit) ? hit : obj.Select(p => Find(p.Value, name)).FirstOrDefault(n => n != null),
        JsonArray array => array.Select(n => Find(n, name)).FirstOrDefault(n => n != null),
        _ => null,
    };
}
