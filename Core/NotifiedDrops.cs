namespace ZeTwitchMiner.Core;

// Дропы, о которых уже показывали уведомление. Хранится между запусками,
// чтобы после перезапуска программа не напоминала о старых наградах.
public sealed class NotifiedDrops
{
    private const int Limit = 1000;
    private static string FilePath => Path.Combine(AppPaths.DataDir, "notified.txt");

    private readonly List<string> _order;
    private readonly HashSet<string> _ids;

    public NotifiedDrops()
    {
        try
        {
            _order = File.Exists(FilePath) ? File.ReadAllLines(FilePath).Where(l => l.Length > 0).ToList() : [];
        }
        catch
        {
            _order = [];
        }
        _ids = [.. _order];
    }

    // true, если о дропе ещё не уведомляли
    public bool Add(string id)
    {
        if (!_ids.Add(id)) return false;
        _order.Add(id);
        if (_order.Count > Limit)
        {
            _ids.Remove(_order[0]);
            _order.RemoveAt(0);
        }
        try { File.WriteAllLines(FilePath, _order); }
        catch (Exception ex) { Log.Debug("notified.txt not saved: " + ex.Message); }
        return true;
    }
}
