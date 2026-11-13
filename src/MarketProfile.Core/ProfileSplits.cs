namespace MarketProfile.Core;

/// <summary>
/// Punkty rozbicia profili (jak w WindoTraderze: profil sesji można rozbić na części od wybranego okresu TPO).
///
/// Punkt rozbicia zapamiętujemy jako CZAS (czas lokalny giełdy), a nie numer okresu – dzięki temu
/// rozbicia nie "rozjeżdżają się" po zmianie długości okresu TPO w ustawieniach.
/// Każda część rozbitego profilu ma własne POC, Value Area i Initial Balance.
/// </summary>
public sealed class ProfileSplits
{
    private readonly SortedSet<DateTime> _splitTimes = [];

    /// <summary>Zdarzenie wywoływane po dodaniu lub usunięciu punktu rozbicia.</summary>
    public event EventHandler? Changed;

    /// <summary>Wszystkie punkty rozbicia (do zapisu w pliku).</summary>
    public IReadOnlyList<DateTime> SplitTimes => [.. _splitTimes];

    /// <summary>Zastępuje punkty rozbicia wczytanymi z pliku.</summary>
    public void Load(IEnumerable<DateTime> splitTimes)
    {
        _splitTimes.Clear();
        _splitTimes.UnionWith(splitTimes);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Rozbija profil tak, że okres <paramref name="periodIndex"/> rozpoczyna nową część.
    /// Rozbicie na pierwszym okresie sesji nie ma sensu i jest ignorowane.
    /// </summary>
    public bool Split(SessionProfile profile, int periodIndex)
    {
        if (periodIndex <= 0 || !_splitTimes.Add(profile.GetPeriodStart(periodIndex)))
        {
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Łączy część profilu zaczynającą się od okresu <paramref name="firstPeriod"/> z poprzednią częścią
    /// (czyli usuwa punkt rozbicia na tym okresie).
    /// </summary>
    public bool MergeWithPrevious(SessionProfile profile, int firstPeriod)
    {
        var removed = _splitTimes.RemoveWhere(t =>
            t > profile.SessionStart && t < profile.SessionEnd && profile.GetPeriodIndex(t) == firstPeriod);

        if (removed == 0)
        {
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Numery okresów, od których zaczynają się kolejne części profilu (bez zera, posortowane).</summary>
    public IReadOnlyList<int> GetSplitPeriods(SessionProfile profile) =>
        _splitTimes
            .Where(t => t > profile.SessionStart && t < profile.SessionEnd)
            .Select(profile.GetPeriodIndex)
            .Where(p => p > 0)
            .Distinct()
            .Order()
            .ToList();

    /// <summary>
    /// Tworzy migawki dla profilu sesji: jedną, jeśli profil nie jest rozbity,
    /// albo po jednej dla każdej części rozbitego profilu.
    /// </summary>
    public IReadOnlyList<ProfileSnapshot> CreateSnapshots(SessionProfile profile)
    {
        var splits = GetSplitPeriods(profile);
        if (splits.Count == 0)
        {
            return [profile.GetSnapshot()];
        }

        // Granice części: [0, s1-1], [s1, s2-1], ..., [sN, koniec].
        var starts = new List<int> { 0 };
        starts.AddRange(splits);

        var result = new List<ProfileSnapshot>();
        for (var i = 0; i < starts.Count; i++)
        {
            var from = starts[i];
            var to = i + 1 < starts.Count ? starts[i + 1] - 1 : int.MaxValue;

            result.Add(profile.GetSnapshot(from, to) with { PartNumber = i + 1, PartCount = starts.Count });
        }

        return result;
    }
}
