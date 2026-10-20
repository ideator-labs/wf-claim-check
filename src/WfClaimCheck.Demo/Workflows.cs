using System.Security.Cryptography;
using System.Text;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace WfClaimCheck.Demo;

public class ResearchData
{
    public string RunKey { get; set; } = "";
    public int DocCount { get; set; }
    public int DocChars { get; set; }
    public List<string> Documents { get; set; } = new();
    public string SourceSha { get; set; } = "";
    public string SeenSha { get; set; } = "";
    public long SeenChars { get; set; }
    public string Summary { get; set; } = "";

    public static string Sha(IEnumerable<string> docs) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", docs)))).ToLowerInvariant();
}

/// <summary>
/// STUB for an agent tool call that pulls source documents into workflow data
/// (retrieved pages, transcripts, model output). Deterministic text, no network.
/// </summary>
public class FetchSources : StepBody
{
    public override ExecutionResult Run(IStepExecutionContext context)
    {
        var data = (ResearchData)context.Workflow.Data;
        var line = "Workflow Core keeps workflow data in the instance row. ";
        data.Documents = Enumerable.Range(1, data.DocCount)
            .Select(i =>
            {
                var sb = new StringBuilder($"doc-{i}: ");
                while (sb.Length < data.DocChars) sb.Append(line);
                return sb.ToString(0, data.DocChars);
            })
            .ToList();
        data.SourceSha = ResearchData.Sha(data.Documents);
        return ExecutionResult.Next();
    }
}

/// <summary>
/// STUB for a model call that reads every document. It records what it actually
/// received so the demo can prove the full payload came back after a restart.
/// </summary>
public class Summarize : StepBody
{
    public override ExecutionResult Run(IStepExecutionContext context)
    {
        var data = (ResearchData)context.Workflow.Data;
        data.SeenSha = ResearchData.Sha(data.Documents);
        data.SeenChars = data.Documents.Sum(d => (long)d.Length);
        data.Summary = $"{data.Documents.Count} documents, {data.SeenChars} chars";
        return ExecutionResult.Next();
    }
}

public class ResearchWorkflow : IWorkflow<ResearchData>
{
    public string Id => "Research";
    public int Version => 1;

    public void Build(IWorkflowBuilder<ResearchData> builder)
    {
        builder
            .StartWith<FetchSources>()
            .WaitFor("approval", data => data.RunKey)
            .Then<Summarize>();
    }
}
