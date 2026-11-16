using EliteSIP.Audio;

namespace EliteSIP.Audio.Pulse.Tests;

internal sealed class PulseManualScheduler
{
    private readonly List<Entry> _pending = [];

    public TimeSpan? LastDelay { get; private set; }

    /// <summary>Сколько работ ждёт своего часа.</summary>
    public int PendingCount => _pending.Count(e => !e.Cancelled);

    /// <summary>Сколько работ было отменено.</summary>
    public int CancelledCount { get; private set; }

    public IDisposable Schedule(TimeSpan delay, Action work)
    {
        LastDelay = delay;
        Entry entry = new(work, this);
        _pending.Add(entry);
        return entry;
    }

    /// <summary>Доводит время до срока: выполняет всё, что не отменили.</summary>
    public void Fire()
    {
        Entry[] due = [.. _pending];
        _pending.Clear();
        foreach (Entry entry in due)
        {
            if (!entry.Cancelled)
            {
                entry.Work();
            }
        }
    }

    /// <summary>
    /// Выполняет работу, даже если её отменили.
    ///
    /// Так ведут себя и таймер, и очередь оригинала: отмена не останавливает
    /// то, что уже началось. Ради этого случая в шине стоит повторная проверка
    /// владения, и без такого способа её было бы нечем проверить.
    /// </summary>
    public void FireEvenIfCancelled()
    {
        Entry[] due = [.. _pending];
        _pending.Clear();
        foreach (Entry entry in due)
        {
            entry.Work();
        }
    }

    private sealed class Entry(Action work, PulseManualScheduler owner) : IDisposable
    {
        public Action Work { get; } = work;
        public bool Cancelled { get; private set; }

        public void Dispose()
        {
            if (Cancelled)
            {
                return;
            }

            Cancelled = true;
            owner.CancelledCount++;
        }
    }
}
