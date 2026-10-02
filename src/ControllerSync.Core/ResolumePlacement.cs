using System.Globalization;
using System.Text.Json;

namespace ControllerSync.Core;

public sealed record ClipFrame(double? Width, double? Height, double? X, double? Y)
{
    public bool Any => Width != null || Height != null || X != null || Y != null;

    public string Describe()
    {
        var parts = new List<string>();
        if (Width is double width && Height is double height)
            parts.Add(Format(width) + "×" + Format(height));
        else if (Width is double onlyWidth)
            parts.Add("width " + Format(onlyWidth));
        else if (Height is double onlyHeight)
            parts.Add("height " + Format(onlyHeight));
        if (X is double x)
            parts.Add("X " + Format(x));
        if (Y is double y)
            parts.Add("Y " + Format(y));
        return parts.Count == 0 ? "" : string.Join(", ", parts);
    }

    private static string Format(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}

public readonly record struct ResolumeParameterWrite(long Id, string Label, string Body);

public sealed record PlacementPlan(
    IReadOnlyList<ResolumeParameterWrite> Writes,
    bool NeedsTransform,
    IReadOnlyList<string> Missing);

public static class ResolumePlacement
{
    public const string TransformEffect = "effect:///video/Transform";
    public const double Limit = 32768;

    public static bool TryParse(string? width, string? height, string? x, string? y, out ClipFrame? frame, out string? error)
    {
        frame = null;
        if (!TryOptional(width, "Width", 0, false, out var widthValue, out error))
            return false;
        if (!TryOptional(height, "Height", 0, false, out var heightValue, out error))
            return false;
        if (!TryOptional(x, "X", -Limit, true, out var xValue, out error))
            return false;
        if (!TryOptional(y, "Y", -Limit, true, out var yValue, out error))
            return false;

        frame = new ClipFrame(widthValue, heightValue, xValue, yValue);
        error = null;
        return true;
    }

    public static PlacementPlan Read(string clipJson, ClipFrame frame)
    {
        using var document = JsonDocument.Parse(clipJson);
        var root = document.RootElement;
        var writes = new List<ResolumeParameterWrite>();
        var missing = new List<string>();
        if (!root.TryGetProperty("video", out var video) || video.ValueKind != JsonValueKind.Object)
        {
            if (frame.Width != null)
                missing.Add("width");
            if (frame.Height != null)
                missing.Add("height");
            return new PlacementPlan(writes, frame.X != null || frame.Y != null, missing);
        }

        AddRange(video, "width", frame.Width, "width", writes, missing);
        AddRange(video, "height", frame.Height, "height", writes, missing);
        if (frame.Width != null || frame.Height != null)
            AddChoice(video, "resize", "Stretch", writes);

        long? xId = null;
        long? yId = null;
        if (video.TryGetProperty("effects", out var effects) && effects.ValueKind == JsonValueKind.Array)
        {
            foreach (var effect in effects.EnumerateArray())
            {
                var name = effect.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                if (!string.Equals(name, "Transform", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (effect.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object)
                {
                    xId = ParameterId(parameters, "Position X") ?? ParameterId(parameters, "positionx");
                    yId = ParameterId(parameters, "Position Y") ?? ParameterId(parameters, "positiony");
                }

                break;
            }
        }

        if (frame.X is double x && xId is long foundX)
            writes.Add(Range(foundX, "X", x));
        if (frame.Y is double y && yId is long foundY)
            writes.Add(Range(foundY, "Y", y));

        var needsTransform = (frame.X != null && xId == null) || (frame.Y != null && yId == null);
        return new PlacementPlan(writes, needsTransform, missing);
    }

    private static bool TryOptional(string? text, string label, double minimum, bool allowZero, out double? value, out string? error)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        var normalized = text.Trim().Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || double.IsNaN(number) || double.IsInfinity(number))
        {
            error = label + " must be a number of pixels.";
            return false;
        }

        if (number > Limit || number < minimum || (!allowZero && number <= 0))
        {
            error = allowZero
                ? label + " must be from " + minimum.ToString("0", CultureInfo.InvariantCulture) + " to 32768."
                : label + " must be from 1 to 32768 pixels.";
            return false;
        }

        value = number;
        return true;
    }

    private static void AddRange(JsonElement parent, string property, double? number, string label, List<ResolumeParameterWrite> writes, List<string> missing)
    {
        if (number is not double value)
            return;
        var id = ParameterId(parent, property);
        if (id == null)
        {
            missing.Add(label);
            return;
        }

        writes.Add(Range(id.Value, label, value));
    }

    private static void AddChoice(JsonElement parent, string property, string choice, List<ResolumeParameterWrite> writes)
    {
        var id = ParameterId(parent, property);
        if (id == null)
            return;
        writes.Add(new ResolumeParameterWrite(
            id.Value,
            "size mode",
            "{\"id\":" + id.Value + ",\"valuetype\":\"ParamChoice\",\"value\":" + JsonSerializer.Serialize(choice) + "}"));
    }

    private static ResolumeParameterWrite Range(long id, string label, double value) =>
        new(
            id,
            label,
            "{\"id\":" + id + ",\"valuetype\":\"ParamRange\",\"value\":" + value.ToString(CultureInfo.InvariantCulture) + "}");

    private static long? ParameterId(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var parameter) || parameter.ValueKind != JsonValueKind.Object)
            return null;
        if (!parameter.TryGetProperty("id", out var id) || !id.TryGetInt64(out var value))
            return null;
        return value;
    }
}
