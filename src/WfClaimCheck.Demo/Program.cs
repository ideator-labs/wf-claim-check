using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WfClaimCheck;
using WfClaimCheck.Demo;
using WorkflowCore.Interface;
using WorkflowCore.Models;
using WorkflowCore.Persistence.EntityFramework.Services;
using WorkflowCore.Persistence.Sqlite;

// Usage:
//   baseline <db>                 plain UseSqlite, start a large run, stop while it waits
//   start    <db> <blobs> <state> claim check on, start a large and a small run, stop while they wait
//   resume   <db> <blobs> <state> new process, publish approvals, wait for Complete
//   inspect  <db>                 raw Workflow rows as JSON, read with plain SQL
//   load     <db> <blobs> <id>    load one instance through the claim check provider, print its data type

var mode = args.ElementAtOrDefault(0);
switch (mode)
{
    case "baseline":
        await Baseline(args[1]);
        break;
    case "start":
        await Start(args[1], args[2], args[3]);
        break;
    case "resume":
        await Resume(args[1], args[2], args[3]);
        break;
    case "inspect":
        Inspect(args[1]);
        break;
    case "load":
        return await Load(args[1], args[2], args[3]);
    default:
        Console.Error.WriteLine("usage: baseline|start|resume|inspect ...");
        return 2;
}
return 0;

static ServiceProvider BuildHost(string db, string? blobs)
{
    var services = new ServiceCollection();
    services.AddLogging(l => l.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace).SetMinimumLevel(LogLevel.Warning));
    var cs = $"Data Source={db}";
    Func<IServiceProvider, IPersistenceProvider> sqlite = _ => new EntityFrameworkPersistenceProvider(new SqliteContextFactory(cs), true, false);
    services.AddWorkflow(o =>
    {
        if (blobs == null) o.UsePersistence(sqlite);
        else o.UseClaimCheck(sqlite, new FileSystemPayloadStore(blobs), c => c.ThresholdBytes = 64 * 1024);
        o.UsePollInterval(TimeSpan.FromMilliseconds(500));
    });
    var sp = services.BuildServiceProvider();
    var host = sp.GetRequiredService<IWorkflowHost>();
    host.RegisterWorkflow<ResearchWorkflow, ResearchData>();
    host.Start();
    return sp;
}

static async Task<WorkflowInstance> WaitUntil(IServiceProvider sp, string id, Func<WorkflowInstance, bool> done, string what)
{
    var store = sp.GetRequiredService<IPersistenceProvider>();
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (DateTime.UtcNow < deadline)
    {
        var wf = await store.GetWorkflowInstance(id);
        if (done(wf)) return wf;
        await Task.Delay(200);
    }
    throw new TimeoutException($"workflow {id} never reached: {what}");
}

static bool Waiting(WorkflowInstance wf) => wf.ExecutionPointers.Any(p => p.Status == PointerStatus.WaitingForEvent);

static async Task<string> StartRun(IServiceProvider sp, int docCount, int docChars, string runKey)
{
    var host = sp.GetRequiredService<IWorkflowHost>();
    var id = await host.StartWorkflow("Research", 1, new ResearchData { RunKey = runKey, DocCount = docCount, DocChars = docChars });
    await WaitUntil(sp, id, Waiting, "waiting for approval");
    return id;
}

static async Task Baseline(string db)
{
    using var sp = BuildHost(db, blobs: null);
    var id = await StartRun(sp, 30, 100_000, "baseline");
    sp.GetRequiredService<IWorkflowHost>().Stop();
    Console.WriteLine(JsonSerializer.Serialize(new { large = id }));
}

static async Task Start(string db, string blobs, string statePath)
{
    using var sp = BuildHost(db, blobs);
    var large = await StartRun(sp, 30, 100_000, "large");
    var small = await StartRun(sp, 2, 100, "small");
    sp.GetRequiredService<IWorkflowHost>().Stop();
    var state = JsonSerializer.Serialize(new { large, small });
    File.WriteAllText(statePath, state);
    Console.WriteLine(state);
}

static async Task Resume(string db, string blobs, string statePath)
{
    var state = JsonDocument.Parse(File.ReadAllText(statePath)).RootElement;
    using var sp = BuildHost(db, blobs);
    var host = sp.GetRequiredService<IWorkflowHost>();
    var report = new Dictionary<string, object>();
    foreach (var (name, key) in new[] { ("large", "large"), ("small", "small") })
    {
        var id = state.GetProperty(name).GetString()!;
        await host.PublishEvent("approval", key, "approved");
        var wf = await WaitUntil(sp, id, w => w.Status != WorkflowStatus.Runnable, "a final status");
        var data = (ResearchData)wf.Data;
        report[name] = new { id, status = wf.Status.ToString(), data.SourceSha, data.SeenSha, data.SeenChars, data.Summary };
    }
    host.Stop();
    Console.WriteLine(JsonSerializer.Serialize(report));
}

static void Inspect(string db)
{
    using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT InstanceId, Status, length(CAST(Data AS BLOB)), Data FROM Workflow";
    using var r = cmd.ExecuteReader();
    var rows = new List<object>();
    while (r.Read())
    {
        var data = r.GetString(3);
        rows.Add(new { id = r.GetString(0), status = r.GetInt32(1), dataBytes = r.GetInt64(2), data = data.Length <= 1024 ? data : null });
    }
    Console.WriteLine(JsonSerializer.Serialize(rows));
}

static async Task<int> Load(string db, string blobs, string id)
{
    var inner = new EntityFrameworkPersistenceProvider(new SqliteContextFactory($"Data Source={db}"), false, false);
    var store = new ClaimCheckPersistenceProvider(inner, new FileSystemPayloadStore(blobs), new ClaimCheckOptions());
    try
    {
        var wf = await store.GetWorkflowInstance(id);
        Console.WriteLine(wf.Data.GetType().FullName);
        return 0;
    }
    catch (InvalidDataException ex)
    {
        Console.WriteLine($"refused: {ex.Message}");
        return 3;
    }
}
