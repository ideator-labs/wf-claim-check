using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace WfClaimCheck;

/// <summary>
/// Wraps any Workflow Core persistence provider. Workflow data whose JSON is larger
/// than <see cref="ClaimCheckOptions.ThresholdBytes"/> is written to an
/// <see cref="IPayloadStore"/> and the row stores a <see cref="ClaimTicket"/> instead.
/// Loads swap the ticket back for the real data before the host sees the instance.
/// </summary>
public sealed class ClaimCheckPersistenceProvider : IPersistenceProvider
{
    private static readonly JsonSerializerSettings Json = new() { TypeNameHandling = TypeNameHandling.All };

    // The host loads an instance, runs it, then persists the same object. Remembering which
    // ticket each loaded object came from lets us delete the superseded payload after the
    // row points at the new one. Keyed by reference, so it is safe across pooled providers.
    private static readonly ConditionalWeakTable<WorkflowInstance, ClaimTicket> Loaded = new();

    private readonly IPersistenceProvider _inner;
    private readonly IPayloadStore _store;
    private readonly ClaimCheckOptions _options;

    public ClaimCheckPersistenceProvider(IPersistenceProvider inner, IPayloadStore store, ClaimCheckOptions options)
    {
        _inner = inner;
        _store = store;
        _options = options;
    }

    public Task<string> CreateNewWorkflow(WorkflowInstance workflow, CancellationToken ct = default) =>
        WithTicket(workflow, () => _inner.CreateNewWorkflow(workflow, ct), ct);

    public Task PersistWorkflow(WorkflowInstance workflow, CancellationToken ct = default) =>
        WithTicket(workflow, async () => { await _inner.PersistWorkflow(workflow, ct); return 0; }, ct);

    public Task PersistWorkflow(WorkflowInstance workflow, List<EventSubscription> subscriptions, CancellationToken ct = default) =>
        WithTicket(workflow, async () => { await _inner.PersistWorkflow(workflow, subscriptions, ct); return 0; }, ct);

    public async Task<WorkflowInstance> GetWorkflowInstance(string id, CancellationToken ct = default) =>
        await Rehydrate(await _inner.GetWorkflowInstance(id, ct), ct);

    public async Task<IEnumerable<WorkflowInstance>> GetWorkflowInstances(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var result = new List<WorkflowInstance>();
        foreach (var wf in await _inner.GetWorkflowInstances(ids, ct))
            result.Add(await Rehydrate(wf, ct));
        return result;
    }

    [Obsolete]
    public async Task<IEnumerable<WorkflowInstance>> GetWorkflowInstances(WorkflowStatus? status, string type, DateTime? createdFrom, DateTime? createdTo, int skip, int take)
    {
        var result = new List<WorkflowInstance>();
        foreach (var wf in await _inner.GetWorkflowInstances(status, type, createdFrom, createdTo, skip, take))
            result.Add(await Rehydrate(wf, default));
        return result;
    }

    private async Task<T> WithTicket<T>(WorkflowInstance workflow, Func<Task<T>> write, CancellationToken ct)
    {
        Loaded.TryGetValue(workflow, out var previous);
        var data = workflow.Data;
        var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data, Json));

        ClaimTicket? ticket = null;
        if (payload.Length > _options.ThresholdBytes)
        {
            var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            if (previous != null && previous.Sha256 == sha)
            {
                ticket = previous;
            }
            else
            {
                ticket = new ClaimTicket { Key = Guid.NewGuid().ToString("N"), Sha256 = sha, Bytes = payload.Length };
                await _store.PutAsync(ticket.Key, payload, ct);
            }
            workflow.Data = ticket;
        }

        T result;
        try
        {
            result = await write();
        }
        finally
        {
            workflow.Data = data;
        }

        // The row now points at the new ticket (or holds the data inline), so the old
        // payload is unreachable. Delete it only after the row write succeeded.
        if (previous != null && previous.Key != ticket?.Key)
            await _store.DeleteAsync(previous.Key, ct);

        if (ticket != null) Loaded.AddOrUpdate(workflow, ticket);
        else Loaded.Remove(workflow);
        return result;
    }

    private async Task<WorkflowInstance> Rehydrate(WorkflowInstance workflow, CancellationToken ct)
    {
        if (workflow?.Data is not ClaimTicket ticket) return workflow!;

        var payload = await _store.GetAsync(ticket.Key, ct);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (sha != ticket.Sha256)
            throw new InvalidDataException($"Claim check payload {ticket.Key} for workflow {workflow.Id} failed its SHA-256 check.");

        workflow.Data = JsonConvert.DeserializeObject(Encoding.UTF8.GetString(payload), Json)!;
        Loaded.AddOrUpdate(workflow, ticket);
        return workflow;
    }

    public Task<IEnumerable<string>> GetRunnableInstances(DateTime asAt, CancellationToken ct = default) => _inner.GetRunnableInstances(asAt, ct);
    public Task<string> CreateEventSubscription(EventSubscription subscription, CancellationToken ct = default) => _inner.CreateEventSubscription(subscription, ct);
    public Task<IEnumerable<EventSubscription>> GetSubscriptions(string eventName, string eventKey, DateTime asOf, CancellationToken ct = default) => _inner.GetSubscriptions(eventName, eventKey, asOf, ct);
    public Task TerminateSubscription(string eventSubscriptionId, CancellationToken ct = default) => _inner.TerminateSubscription(eventSubscriptionId, ct);
    public Task<EventSubscription> GetSubscription(string eventSubscriptionId, CancellationToken ct = default) => _inner.GetSubscription(eventSubscriptionId, ct);
    public Task<EventSubscription> GetFirstOpenSubscription(string eventName, string eventKey, DateTime asOf, CancellationToken ct = default) => _inner.GetFirstOpenSubscription(eventName, eventKey, asOf, ct);
    public Task<bool> SetSubscriptionToken(string eventSubscriptionId, string token, string workerId, DateTime expiry, CancellationToken ct = default) => _inner.SetSubscriptionToken(eventSubscriptionId, token, workerId, expiry, ct);
    public Task ClearSubscriptionToken(string eventSubscriptionId, string token, CancellationToken ct = default) => _inner.ClearSubscriptionToken(eventSubscriptionId, token, ct);
    public Task<string> CreateEvent(Event newEvent, CancellationToken ct = default) => _inner.CreateEvent(newEvent, ct);
    public Task<Event> GetEvent(string id, CancellationToken ct = default) => _inner.GetEvent(id, ct);
    public Task<IEnumerable<string>> GetRunnableEvents(DateTime asAt, CancellationToken ct = default) => _inner.GetRunnableEvents(asAt, ct);
    public Task<IEnumerable<string>> GetEvents(string eventName, string eventKey, DateTime asOf, CancellationToken ct = default) => _inner.GetEvents(eventName, eventKey, asOf, ct);
    public Task MarkEventProcessed(string id, CancellationToken ct = default) => _inner.MarkEventProcessed(id, ct);
    public Task MarkEventUnprocessed(string id, CancellationToken ct = default) => _inner.MarkEventUnprocessed(id, ct);
    public bool SupportsScheduledCommands => _inner.SupportsScheduledCommands;
    public Task ScheduleCommand(ScheduledCommand command) => _inner.ScheduleCommand(command);
    public Task ProcessCommands(DateTimeOffset asOf, Func<ScheduledCommand, Task> action, CancellationToken ct = default) => _inner.ProcessCommands(asOf, action, ct);
    public Task PersistErrors(IEnumerable<ExecutionError> errors, CancellationToken ct = default) => _inner.PersistErrors(errors, ct);
    public void EnsureStoreExists() => _inner.EnsureStoreExists();
}
