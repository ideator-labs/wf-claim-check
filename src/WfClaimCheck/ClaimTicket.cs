namespace WfClaimCheck;

/// <summary>
/// Stored in the workflow row in place of workflow data that crossed the threshold.
/// The real data lives in the payload store under <see cref="Key"/>.
/// </summary>
public sealed class ClaimTicket
{
    public string Key { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Bytes { get; set; }
}
