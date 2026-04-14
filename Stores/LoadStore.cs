using System.Text.Json;

public static class LoadStore
{
    private static readonly string Path = "data/completed_loads.json";

    public static List<object> LoadAll()
    {
        if (!File.Exists(Path)) return new List<object>();
        return JsonSerializer.Deserialize<List<object>>(File.ReadAllText(Path)) ?? new();
    }

    public static void Save(object load)
    {
        var list = LoadAll();
        list.Add(load);
        File.WriteAllText(Path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }
}
