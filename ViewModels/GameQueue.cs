using System.Collections.ObjectModel;
using ZeTwitchMiner.Core;
using ZeTwitchMiner.Twitch;

namespace ZeTwitchMiner.ViewModels;

// Очередь игр одна на всю программу: её правят и кампании, и добыча, и настройки
public sealed class GameQueue
{
    private readonly Settings _settings;
    private readonly Miner _miner;

    public ObservableCollection<string> Games { get; }
    public event Action? Changed;

    public GameQueue(Settings settings, Miner miner)
    {
        _settings = settings;
        _miner = miner;
        Games = new ObservableCollection<string>(settings.PriorityGames);
    }

    public bool Contains(string game) => Games.Contains(game);

    public int IndexOf(string game) => Games.IndexOf(game);

    public void Add(string game)
    {
        game = game.Trim();
        if (game.Length == 0 || Games.Contains(game)) return;
        Games.Add(game);
        _settings.ExcludedGames.Remove(game);
        Commit();
    }

    public void Remove(string game)
    {
        if (Games.Remove(game)) Commit();
    }

    public void Toggle(string game)
    {
        if (Contains(game)) Remove(game);
        else Add(game);
    }

    public void Move(string game, int delta)
    {
        var i = Games.IndexOf(game);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Games.Count) return;
        Games.Move(i, j);
        Commit();
    }

    // Исключение убирает игру из очереди
    public void Exclude(string game)
    {
        if (!_settings.ExcludedGames.Contains(game)) _settings.ExcludedGames.Add(game);
        Games.Remove(game);
        Commit();
    }

    public void Unexclude(string game)
    {
        if (_settings.ExcludedGames.Remove(game)) Commit();
    }

    // Майнер сразу пересчитывает, что добывать
    private void Commit()
    {
        _settings.PriorityGames = Games.ToList();
        _settings.Save();
        Changed?.Invoke();
        _miner.Reload();
    }
}
