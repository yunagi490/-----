using System.Text.Json;
namespace KyoshinMonitor.Map;

public readonly record struct GeoPoint(double Lon, double Lat);

/// <summary>1つのポリゴン (外周 + 穴)</summary>
public sealed record GeoPolygon(IReadOnlyList<GeoPoint[]> Rings);

/// <summary>Polygon / MultiPolygon の GeoJSON (WGS84, [経度, 緯度]) を読み込む</summary>
public sealed class GeoJsonMap
{
    public IReadOnlyList<GeoPolygon> Polygons { get; }

    private GeoJsonMap(List<GeoPolygon> polygons) => Polygons = polygons;

    public static GeoJsonMap Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var polygons = new List<GeoPolygon>();
        var root = doc.RootElement;

        if (root.TryGetProperty("features", out var features))
        {
            foreach (var f in features.EnumerateArray())
            {
                if (f.TryGetProperty("geometry", out var geometry)) AddGeometry(geometry, polygons);
            }
        }
        else if (root.TryGetProperty("geometry", out var single))
        {
            AddGeometry(single, polygons);
        }
        else
        {
            AddGeometry(root, polygons);
        }

        if (polygons.Count == 0)
            throw new InvalidDataException("ポリゴンが見つかりません (Polygon/MultiPolygon のGeoJSONが必要です)");
        return new GeoJsonMap(polygons);
    }

    private static void AddGeometry(JsonElement g, List<GeoPolygon> polygons)
    {
        if (g.ValueKind != JsonValueKind.Object || !g.TryGetProperty("type", out var typeEl)) return;
        switch (typeEl.GetString())
        {
            case "Polygon":
                polygons.Add(ReadPolygon(g.GetProperty("coordinates")));
                break;
            case "MultiPolygon":
                foreach (var p in g.GetProperty("coordinates").EnumerateArray())
                    polygons.Add(ReadPolygon(p));
                break;
            case "GeometryCollection":
                foreach (var child in g.GetProperty("geometries").EnumerateArray())
                    AddGeometry(child, polygons);
                break;
        }
    }

    private static GeoPolygon ReadPolygon(JsonElement rings)
    {
        var list = new List<GeoPoint[]>();
        foreach (var ring in rings.EnumerateArray())
        {
            var pts = new List<GeoPoint>();
            foreach (var p in ring.EnumerateArray())
                pts.Add(new GeoPoint(p[0].GetDouble(), p[1].GetDouble()));
            list.Add(pts.ToArray());
        }
        return new GeoPolygon(list);
    }
}