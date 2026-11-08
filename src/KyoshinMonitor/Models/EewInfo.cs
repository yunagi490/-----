using System.Text.Json;

public sealed record EewInfo(
  string EventId,
  int Serial,
  string Hypocenter,
  double Magnitude,
  int Depth,
  string MaxIntensity,
  bool IsWarn,
  bool IsFinal,
  bool IsCancel,
  bool IsTraining,
  DateTime AnnouncedAt,
  DateTime ReceivedAt)
{
  public string Key => $"{EventId}#{Serial}";

  public static EewInfo? Parse(string json)
  {
    using var doc = JsonDocument.Parse(json);
    var r = doc.RootElement;
    if (r.ValueKind != JsonValueKind.Object) return null;
    if (!r.TryGetProperty("type", out var type) || type.GetString() != "jma_eew") return null;

    var announced = DateTime.TryParseExact(Str(r, "AnnouncedTime"), "yyyy/MM/dd HH:mm:ss",
        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var a)
        ? a : DateTime.MinValue;

    return new EewInfo(
        Str(r, "EventID"),
        (int)Num(r, "Serial"),
        Str(r, "Hypocenter"),
        Num(r, "Magunitude"), // Wolfx側のスペルがこの綴り
        (int)Num(r, "Depth"),
        Str(r, "MaxIntensity"),
        Bool(r, "isWarn"),
        Bool(r, "isFinal"),
        Bool(r, "isCancel"),
        Bool(r, "isTraining"),
        announced,
        DateTime.Now);
  }
    private static string Str(JsonElement e, string name) =>
      e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
  
  private static double Num(JsonElement e, string name)
  {
      if (!e.TryGetProperty(name, out var v)) return 0;
      if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
      if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var d)) return d;
      return 0;
  }
  
  private static bool Bool(JsonElement e, string name) =>
      e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
