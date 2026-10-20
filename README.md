# WfClaimCheck

Claim check for [Workflow Core](https://github.com/danielgerlag/workflow-core) workflow data.

Workflow Core serializes the whole data object into the workflow row on every persist. Agent workflows put retrieved documents, transcripts and model output in that object. Rows grow to megabytes, and MongoDB refuses anything over 16 MB ([workflow-core#1082](https://github.com/danielgerlag/workflow-core/issues/1082), "Workflow is too large to save").

WfClaimCheck wraps any `IPersistenceProvider`. When the data JSON is larger than a threshold, it writes the JSON to a payload store and saves a small `ClaimTicket` (key, SHA-256, size) in the row. Loads swap the ticket back for the real data, after checking the hash. Steps never see the ticket.

```csharp
services.AddWorkflow(o => o.UseClaimCheck(
    sp => new EntityFrameworkPersistenceProvider(new SqliteContextFactory(cs), true, false),
    new FileSystemPayloadStore("payloads"),
    c => c.ThresholdBytes = 64 * 1024));
```

This is a proof of concept, not a NuGet package. The only payload store is the local file system.

## Run the proof

Needs the .NET 8 SDK and `jq`.

```bash
bash demo.sh          # 9 checks, exit 0
bash demo.sh --break  # removes the ticket swap, checks fail, exit 1
```

The demo runs a real Workflow Core 3.21.0 host on SQLite. A 3 MB run goes from 3,000,457 bytes in the row (plain provider) to 183 bytes (with WfClaimCheck), then finishes in a second process with every byte intact.

Read [AGENTS.md](AGENTS.md) for the data shape, design decisions, limits and next steps.
