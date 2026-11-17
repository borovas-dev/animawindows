using System.Threading.Channels;

namespace AnimaWindows.Data;

/// <summary>
/// In-process table-change notifications. Writers publish the names of the tables they touched after commit; observers
/// subscribe to a set of tables and get a coalesced "something changed" signal.
/// </summary>
internal sealed class ChangeBus
{
    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    public Subscription Subscribe(IReadOnlyCollection<string> tables)
    {
        var sub = new Subscription(this, new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase));
        lock (_gate)
        {
            _subscriptions.Add(sub);
        }

        return sub;
    }

    public void Publish(IEnumerable<string> tables)
    {
        Subscription[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _subscriptions];
        }

        var changed = tables as ICollection<string> ?? tables.ToList();
        foreach (var sub in snapshot)
        {
            if (changed.Any(sub.Tables.Contains))
            {
                sub.Signal();
            }
        }
    }

    private void Remove(Subscription sub)
    {
        lock (_gate)
        {
            _subscriptions.Remove(sub);
        }
    }

    internal sealed class Subscription : IDisposable
    {
        private readonly ChangeBus _bus;
        private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

        public Subscription(ChangeBus bus, HashSet<string> tables)
        {
            _bus = bus;
            Tables = tables;
        }

        public HashSet<string> Tables { get; }

        public ChannelReader<bool> Reader => _channel.Reader;

        public void Signal() => _channel.Writer.TryWrite(true);

        public void Dispose() => _bus.Remove(this);
    }
}
