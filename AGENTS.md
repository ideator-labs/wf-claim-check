# AGENTS.md

Handoff for a coding agent with no prior context.

## What this is

WfClaimCheck is a persistence decorator for Workflow Core (the embeddable .NET workflow engine, https://github.com/danielgerlag/workflow-core). It moves large workflow data out of the workflow row into a payload store and leaves a small ticket in the row.

The claim `bash demo.sh` proves:

1. With the stock SQLite provider, a 3 MB workflow payload sits in the `Workflow.Data` column (control).
2. With `UseClaimCheck`, the same row holds a `ClaimTicket` under 1 KB, and one payload file holds the data.
3. The workflow stops while waiting for an event, a new process resumes it, and the next step sees all 3,000,000 chars with the same SHA-256.
4. Data under the threshold stays inline.
5. When a step changes the data, a new payload replaces the old one and the old file is deleted.
6. A payload edited on disk is refused on load.

## Status

Real:
- Workflow Core 3.21.0 host, `WorkflowCore.Persistence.Sqlite` 3.21.0, EF Core SQLite. Real start, `WaitFor` event, `PublishEvent`, two separate OS processes.
- The decorator, the file payload store, SHA-256 checks.
- Row sizes are read with plain SQL (`inspect` mode), not through the decorator. Payload hashes are checked with `sha256sum` in bash.

Stubbed (labelled in `Workflows.cs`):
- `FetchSources` stands in for an agent tool call that pulls documents. It generates deterministic text, no network.
- `Summarize` stands in for a model call. It hashes and counts what it received.

Not built: Azure Blob, S3 or other remote stores. Execution pointer `EventData` and `PersistenceData` are not offloaded. No NuGet package.

## Data shape

- Workflow data: any object. `ResearchData` in the demo (`RunKey`, `Documents` list, `SourceSha`, `SeenSha`, `SeenChars`, `Summary`).
- Payload: UTF-8 JSON of the data object, Newtonsoft with `TypeNameHandling.All` (the same settings Workflow Core's EF provider uses). File name `<Key>.json`.
- Row value when offloaded: the EF provider serializes a `ClaimTicket` into `Workflow.Data`:
  `{"$type":"WfClaimCheck.ClaimTicket, WfClaimCheck","Key":"<32 hex>","Sha256":"<64 hex>","Bytes":3000454}`
- Row value under the threshold: the normal inline JSON, untouched.

## Repo map

- `src/WfClaimCheck/ClaimTicket.cs`. The ticket stored in the row.
- `src/WfClaimCheck/IPayloadStore.cs`. Store interface and `FileSystemPayloadStore` (temp file then rename).
- `src/WfClaimCheck/ClaimCheckPersistenceProvider.cs`. The decorator. `WithTicket` handles create and persist. `Rehydrate` handles every load path. Everything else delegates.
- `src/WfClaimCheck/ClaimCheckOptions.cs`. `ThresholdBytes` (default 64 KB) and the `UseClaimCheck` extension on `WorkflowOptions`.
- `src/WfClaimCheck.Demo/Workflows.cs`. `ResearchWorkflow`: FetchSources, WaitFor("approval"), Summarize.
- `src/WfClaimCheck.Demo/Program.cs`. Modes `baseline`, `start`, `resume`, `inspect`, `load`.
- `demo.sh`. Builds, runs the modes, asserts 9 checks. `--break` removes the ticket swap.

## Setup, run, verify (fresh clone)

```bash
git clone https://github.com/ideator-labs/wf-claim-check
cd wf-claim-check
dotnet --list-sdks          # needs an 8.0.x SDK; demo.sh also adds ~/.dotnet to PATH if present
jq --version                # needs jq
bash demo.sh                # expect 9 "CHECK PASS" lines, "RESULT: PASS", exit 0
bash demo.sh --break        # expect CHECK FAIL on 3, 4, 8, 9, "RESULT: FAIL", exit 1
```

Outputs land in `.demo/` (git-ignored): `baseline.db`, `claim.db`, `blobs/`, JSON reports and host logs. Expected runtime under 30 seconds after the first NuGet restore.

## Design decisions

- Decorator over `IPersistenceProvider`, not a new provider. Works with any existing provider (EF, Mongo, Cosmos, DynamoDB) and needs no Workflow Core change.
- Swap and restore. The decorator sets `workflow.Data` to the ticket only for the inner write and restores it in `finally`, so the running host never sees a ticket.
- New random key per changed payload, never overwrite. The payload is written before the row. If the row write fails, the row still points at the old payload, which still exists. The old payload is deleted only after the row write succeeds.
- Unchanged data is not rewritten. If the SHA-256 matches the ticket the instance was loaded with, the old ticket is reused.
- The loaded ticket is remembered in a static `ConditionalWeakTable` keyed by the `WorkflowInstance` object. Workflow Core loads an instance, runs it and persists the same object, and providers are pooled and transient, so object identity is the reliable link. If the link is missing, the old payload is orphaned, never deleted early.
- SHA-256 is checked on load. A payload that does not match its ticket throws `InvalidDataException` instead of running a step on wrong data.

## Known limits

- Orphans. Workflow Core's `IWorkflowPurger` deletes rows directly, so purged workflows leave their payload files behind. A crash between the row write and the delete also leaves one orphan.
- Only `WorkflowInstance.Data` is offloaded. Large event payloads (`ExecutionPointer.EventData`) and step `PersistenceData` still go inline.
- `TypeNameHandling.All` on payload load has the same deserialization risk as Workflow Core's own setting (workflow-core#1280). Anyone who can write the payload store can choose types; the SHA check only proves the payload matches the row.
- Only the file system store exists. It is not safe across machines unless the directory is shared storage.
- Tested on SQLite only. Mongo is the provider that hits the hard 16 MB limit and is not exercised here.
- The baseline run can log one `TaskCanceledException` from `WorkflowConsumer` when the host stops. That is the host shutdown, not the decorator.

## Next steps (in priority order)

1. Mongo proof. Run the same demo against `WorkflowCore.Persistence.MongoDB` in a container with a payload over 16 MB. Done when: plain Mongo fails with "Resulting document after update is larger than 16777216" and `UseClaimCheck` completes the run, both asserted by `demo.sh --mongo`.
2. Azure Blob and S3 stores. Implement `IPayloadStore` for both. Done when: an Azurite-backed run passes checks 3 to 9.
3. Purge hook. Delete payloads for purged workflows. Done when: after `IWorkflowPurger.PurgeWorkflows`, the payload directory is empty and a check asserts it.
4. Event payload offload. Apply the same ticket to large `EventData` on events and pointers. Done when: a 3 MB `PublishEvent` payload leaves under 1 KB in `Event.EventData` and the step still receives it.
5. Orphan sweeper. List payload keys not referenced by any row and delete those older than a grace period. Done when: an injected orphan is removed and a live payload is kept.
6. NuGet package `WfClaimCheck` with xUnit tests for `WithTicket` and `Rehydrate`. Done when: `dotnet test` passes and `dotnet pack` produces a package.

## Rules for working here

- `bash demo.sh` is the source of truth. Run it, and `bash demo.sh --break`, before every commit. Normal must exit 0, break must exit 1.
- Keep stubs labelled as stubs. No paid APIs, no network calls in the demo.
- Do not change Workflow Core itself. Everything goes through public extension points.
- Keep prose short and plain. No em-dashes.
- Commit as `danielgerlag <daniel@gerlag.ca>`.

## Context and sources

- workflow-core#1082 "Workflow is too large to save" (open since 2022-08-17, MongoDB 16 MB error): https://github.com/danielgerlag/workflow-core/issues/1082
- Workflow Core EF provider serializes `Data` with `TypeNameHandling.All`: https://github.com/danielgerlag/workflow-core/blob/master/src/providers/WorkflowCore.Persistence.EntityFramework/ExtensionMethods.cs
- Dapr large workflow payloads, marked fixed for runtime and .NET 1.19 on 2026-10-17: https://github.com/dapr/dapr/issues/6014 and https://github.com/dapr/dotnet-sdk/issues/1533
- Durable Task .NET v1.27.0 (2026-10-19) adds large-payload blob auto-purge: https://github.com/microsoft/durabletask-dotnet/releases
- Claim Check pattern: https://learn.microsoft.com/en-us/azure/architecture/patterns/claim-check
