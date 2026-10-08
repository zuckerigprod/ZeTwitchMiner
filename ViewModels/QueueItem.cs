using ZeTwitchMiner.Core;
using ZeTwitchMiner.Twitch;

namespace ZeTwitchMiner.ViewModels;

public enum QueueState { Mining, Waiting, Upcoming, NotLinked, Done, NoCampaigns }

// Строка очереди на экране добычи
public sealed class QueueItem
{
    public required int Number { get; init; }
    public required string Name { get; init; }
    public required QueueState State { get; init; }
    public Campaign? Cover { get; init; }
    public string Detail { get; init; } = "";
    public bool IsFirst { get; init; }
    public bool IsLast { get; init; }

    public bool IsMining => State == QueueState.Mining;
    public bool IsProblem => State is QueueState.NotLinked or QueueState.NoCampaigns;
    public bool IsDone => State is QueueState.Done;
    public bool CanMoveUp => !IsFirst;
    public bool CanMoveDown => !IsLast;

    public string StatusText => State switch
    {
        QueueState.Mining => Loc.T("Queue.Mining"),
        QueueState.Waiting => Loc.T("Queue.Waiting"),
        QueueState.Upcoming => Loc.T("Queue.Upcoming"),
        QueueState.NotLinked => Loc.T("Queue.NotLinked"),
        QueueState.Done => Loc.T("Queue.Done"),
        _ => Loc.T("Queue.NoCampaigns"),
    };

    public static List<QueueItem> Build(IReadOnlyList<string> games, IEnumerable<Campaign> inventory, Campaign? current)
    {
        var all = inventory.ToList();
        var items = new List<QueueItem>();
        for (var i = 0; i < games.Count; i++)
        {
            var name = games[i];
            var campaigns = all.Where(c => c.Game.Name == name && c.RequiredMinutes > 0).ToList();
            var active = campaigns.Where(c => c.Active && !c.Finished).ToList();
            var eligible = active.Where(c => c.Eligible).ToList();

            var state = current?.Game.Name == name ? QueueState.Mining
                : campaigns.Count == 0 ? QueueState.NoCampaigns
                : eligible.Count > 0 ? QueueState.Waiting
                : active.Count > 0 ? QueueState.NotLinked
                : campaigns.Any(c => c.Upcoming) ? QueueState.Upcoming
                : campaigns.All(c => c.Finished) ? QueueState.Done
                : QueueState.NoCampaigns;

            var remaining = eligible.Sum(c => c.RemainingDrops);
            items.Add(new QueueItem
            {
                Number = i + 1,
                Name = name,
                State = state,
                Cover = (state == QueueState.Mining ? current : null) ?? eligible.FirstOrDefault() ?? campaigns.FirstOrDefault(),
                Detail = state is QueueState.Mining or QueueState.Waiting && remaining > 0 ? Loc.F("Queue.DropsLeft", remaining) : "",
                IsFirst = i == 0,
                IsLast = i == games.Count - 1,
            });
        }
        return items;
    }
}
