using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>A run's place in the waiting line and a rough wait, for the run page.</summary>
public sealed record QueuePlace(int Position, TimeSpan EstimatedWait)
{
    /// <summary>"You are 4th in line, about 25 minutes."</summary>
    public string Text => $"You are {Ordinal(Position)} in line, {About(EstimatedWait)}.";

    public static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    /// <summary>Rounded up, to whole minutes below ten and to five minutes above, so it reads as the guess it is.</summary>
    public static string About(TimeSpan wait)
    {
        int minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
        if (minutes >= 10) minutes = (int)Math.Ceiling(minutes / 5.0) * 5;
        if (minutes < 60) return $"about {minutes} minute{(minutes == 1 ? "" : "s")}";
        int hours = minutes / 60, rest = minutes % 60;
        return $"about {hours} hour{(hours == 1 ? "" : "s")}{(rest == 0 ? "" : $" {rest} minutes")}";
    }
}

/// <summary>Whether a new run may join the line. <see cref="Busy"/> is true when the whole line is full, false when this client has enough runs.</summary>
public sealed record RunAdmission(bool Admitted, string? Refusal = null, bool Busy = false, TimeSpan? RetryAfter = null)
{
    public static readonly RunAdmission Ok = new(true);
}

/// <summary>
/// The one waiting line for every kind of review (systematic, multivocal, API and MCP). A slot is handed to the first
/// run in line whose client holds fewer than Runs:MaxRunningPerClient slots, so one client cannot fill every slot
/// while others wait. A new run is admitted (<see cref="TryAdmit"/>) only while its client has fewer than
/// MaxRunningPerClient + MaxQueuedPerClient runs, and while fewer than Runs:MaxQueuedRuns runs wait; beyond that it is
/// refused at once, instead of waiting for hours. The client key is the normalised network address; it is held in
/// memory only, while the run lives, and never written or logged.
/// </summary>
public partial class RunCoordinator
{
    private const int HoldSamples = 20;

    private readonly object _gate = new();
    private int _running;
    private readonly LinkedList<Ticket> _queue = new();
    private readonly Dictionary<Guid, string> _clients = new();
    private readonly Dictionary<Guid, Ticket> _tickets = new();
    private readonly Dictionary<string, int> _runningByClient = new(StringComparer.Ordinal);
    private readonly Queue<TimeSpan> _recentHolds = new();

    /// <summary>Slots in use now.</summary>
    public int Running { get { lock (_gate) return _running; } }

    /// <summary>Runs waiting in line now.</summary>
    public int Queued { get { lock (_gate) return _queue.Count; } }

    /// <summary>
    /// Lets a new run join the line, or refuses it politely. An admitted run takes its place in line at once, so a
    /// hundred clicks at the same moment are counted one by one; <see cref="AcquireSlotAsync"/> then claims that place.
    /// Give the place back with <see cref="Withdraw"/> if the run does not start after all.
    /// </summary>
    public RunAdmission TryAdmit(Guid runId, string? client)
    {
        string key = ClientKey(runId, client);
        RunAdmission refusal;
        lock (_gate)
        {
            if (_clients.ContainsKey(runId)) return RunAdmission.Ok;
            int mine = _clients.Values.Count(c => c == key);
            int allowed = _options.MaxRunningPerClient + _options.MaxQueuedPerClient;
            if (mine >= allowed)
            {
                refusal = new RunAdmission(false,
                    $"You already have {mine} reviews running or waiting. Start this one when one of them has finished.");
            }
            else
            {
                _clients[runId] = key;
                var ticket = Enqueue(runId, key);
                Dispatch();
                if (ticket.Granted.Task.IsCompleted || _queue.Count <= _options.MaxQueuedRuns) return RunAdmission.Ok;
                _queue.Remove(ticket.Node);
                _tickets.Remove(runId);
                _clients.Remove(runId);
                var wait = WaitFor(_queue.Count);
                refusal = new RunAdmission(false,
                    $"The tool is busy: {_queue.Count} reviews are waiting already. Try again later, {QueuePlace.About(wait).Replace("about", "in about")}.",
                    Busy: true, RetryAfter: wait);
            }
        }
        AppLog.For<RunCoordinator>().LogInformation("A new run was refused: {Kind}. {Running} running, {Queued} waiting.",
            refusal.Busy ? "the line is full" : "the client has its share", Running, Queued);
        return refusal;
    }

    /// <summary>Takes a run out of the line, or gives back a slot it was handed and never claimed. Safe to call twice.</summary>
    public void Withdraw(Guid runId)
    {
        lock (_gate)
        {
            _clients.Remove(runId);
            if (!_tickets.Remove(runId, out var ticket)) return;
            if (ticket.Node.List != null)
            {
                _queue.Remove(ticket.Node);
                ticket.Granted.TrySetCanceled();
            }
            else if (ticket.Granted.Task.IsCompletedSuccessfully)
            {
                FreeSlot(ticket);
            }
        }
    }

    /// <summary>1-based place in the waiting line, or 0 when the run is not waiting.</summary>
    public int QueuePosition(Guid runId)
    {
        lock (_gate)
        {
            int i = 1;
            foreach (var ticket in _queue)
            {
                if (ticket.RunId == runId) return i;
                i++;
            }
            return 0;
        }
    }

    /// <summary>The run's place in line with a rough wait, or null when it is not waiting.</summary>
    public QueuePlace? PlaceOf(Guid runId)
    {
        int position = QueuePosition(runId);
        if (position == 0) return null;
        lock (_gate) return new QueuePlace(position, WaitFor(position));
    }

    /// <summary>
    /// Waits for a free slot. The returned handle must be disposed to give the slot back. The line is served in arrival
    /// order, except that a run whose client holds its share of slots lets the next one pass.
    /// </summary>
    public async Task<IDisposable> AcquireSlotAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        Ticket ticket;
        lock (_gate)
        {
            if (!_tickets.TryGetValue(runId, out ticket!))
            {
                ticket = Enqueue(runId, _clients.TryGetValue(runId, out var client) ? client : ClientKey(runId, null));
                Dispatch();
            }
        }

        using (cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                if (ticket.Node.List == null) return;
                _queue.Remove(ticket.Node);
                if (_tickets.TryGetValue(runId, out var current) && current == ticket) _tickets.Remove(runId);
                ticket.Granted.TrySetCanceled();
            }
        }))
        {
            await ticket.Granted.Task.ConfigureAwait(false);
        }
        lock (_gate)
        {
            if (_tickets.TryGetValue(runId, out var current) && current == ticket) _tickets.Remove(runId);
        }
        return new Slot(this, ticket); // Dispatch counted the slot when it handed it over
    }

    /// <summary>As <see cref="AcquireSlotAsync(Guid, CancellationToken)"/>, telling the caller each time the run's place in line changes.</summary>
    public async Task<IDisposable> AcquireSlotAsync(Guid runId, CancellationToken cancellationToken, Func<QueuePlace, Task> onPlace)
    {
        var acquire = AcquireSlotAsync(runId, cancellationToken);
        QueuePlace? shown = null;
        while (!acquire.IsCompleted)
        {
            if (PlaceOf(runId) is { } place && place != shown)
            {
                shown = place;
                await onPlace(place).ConfigureAwait(false);
            }
            await Task.WhenAny(acquire, Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false);
        }
        return await acquire.ConfigureAwait(false);
    }

    private static string ClientKey(Guid runId, string? client) => string.IsNullOrWhiteSpace(client) ? "run:" + runId.ToString("N") : client;

    // Called under _gate.
    private Ticket Enqueue(Guid runId, string client)
    {
        var ticket = new Ticket(runId, client);
        ticket.Node = _queue.AddLast(ticket);
        _tickets[runId] = ticket;
        return ticket;
    }

    // Called under _gate: hands free slots to the first runs in line whose client is below its share.
    private void Dispatch()
    {
        while (_running < _maxConcurrent)
        {
            var node = _queue.First;
            while (node != null && _runningByClient.GetValueOrDefault(node.Value.Client) >= _options.MaxRunningPerClient) node = node.Next;
            if (node == null) return;
            _queue.Remove(node);
            _running++;
            _runningByClient[node.Value.Client] = _runningByClient.GetValueOrDefault(node.Value.Client) + 1;
            node.Value.GrantedUtc = DateTime.UtcNow;
            node.Value.Granted.TrySetResult();
        }
    }

    private void Release(Ticket ticket)
    {
        lock (_gate)
        {
            _recentHolds.Enqueue(DateTime.UtcNow - ticket.GrantedUtc);
            while (_recentHolds.Count > HoldSamples) _recentHolds.Dequeue();
            FreeSlot(ticket);
        }
    }

    // Called under _gate.
    private void FreeSlot(Ticket ticket)
    {
        _running = Math.Max(0, _running - 1);
        int left = _runningByClient.GetValueOrDefault(ticket.Client) - 1;
        if (left > 0) _runningByClient[ticket.Client] = left; else _runningByClient.Remove(ticket.Client);
        Dispatch();
    }

    // Called under _gate. Every slot frees about once per typical hold, so the n-th in line waits about n / slots holds.
    private TimeSpan WaitFor(int position)
    {
        var hold = _recentHolds.Count >= 3
            ? TimeSpan.FromTicks((long)_recentHolds.Average(h => h.Ticks))
            : TimeSpan.FromMinutes(Math.Max(1, _options.TypicalRunMinutes));
        return hold * Math.Ceiling(position / (double)_maxConcurrent);
    }

    private sealed class Ticket
    {
        public Ticket(Guid runId, string client)
        {
            RunId = runId;
            Client = client;
        }

        public Guid RunId { get; }
        public string Client { get; }
        public LinkedListNode<Ticket> Node { get; set; } = null!;
        public TaskCompletionSource Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTime GrantedUtc { get; set; }
    }

    private sealed class Slot : IDisposable
    {
        private RunCoordinator? _owner;
        private readonly Ticket _ticket;

        public Slot(RunCoordinator owner, Ticket ticket)
        {
            _owner = owner;
            _ticket = ticket;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_ticket);
    }
}
