using System.Text.Json;
public sealed record QuakeInfo(
    string Id,
    string Time,
    string Hypocenter,
    double? Magnitude,
    int? Depth,
    int MaxScale,
    string IssueType)
{
    public string ShindoText => Shindo.FromScale(MaxScale);
 
    public static QuakeInfo? Parse(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (!r.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || code.GetInt32() != 551)
            return null;
        if (!r.TryGetProperty("earthquake", out var eq)) return null;
 
        var id = r.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var time = eq.TryGetProperty("time", out var t) ? t.GetString() ?? "" : "";
 
        string name = "";
        double? mag = null;
        int? depth = null;
        if (eq.TryGetProperty("hypocenter", out var h))
        {
            if (h.TryGetProperty("name", out var n)) name = n.GetString() ?? "";
            if (h.TryGetProperty("magnitude", out var m) && m.ValueKind == JsonValueKind.Number && m.GetDouble() >= 0)
                mag = m.GetDouble();
            if (h.TryGetProperty("depth", out var d) && d.ValueKind == JsonValueKind.Number && d.GetInt32() >= 0)
                depth = d.GetInt32();
        }
 
        var scale = eq.TryGetProperty("maxScale", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : -1;
        var issue = r.TryGetProperty("issue", out var iss) && iss.TryGetProperty("type", out var it)
            ? it.GetString() ?? "" : "";
 
        return new QuakeInfo(id, time, name, mag, depth, scale, issue);
    }
 
    public string IssueLabel => IssueType switch
    {
        "ScalePrompt" => "震度速報",
        "Destination" => "震源情報",
        "ScaleAndDestination" => "震源・震度情報",
        "DetailScale" => "各地の震度",
        "Foreign" => "遠地地震",
        _ => IssueType,
    };
}