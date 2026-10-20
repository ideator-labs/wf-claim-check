namespace WfClaimCheck;

public interface IPayloadStore
{
    Task PutAsync(string key, byte[] payload, CancellationToken ct = default);
    Task<byte[]> GetAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public sealed class FileSystemPayloadStore : IPayloadStore
{
    private readonly string _root;

    public FileSystemPayloadStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    private string PathFor(string key) => Path.Combine(_root, key + ".json");

    public async Task PutAsync(string key, byte[] payload, CancellationToken ct = default)
    {
        var tmp = PathFor(key) + ".tmp";
        await File.WriteAllBytesAsync(tmp, payload, ct);
        File.Move(tmp, PathFor(key), overwrite: true);
    }

    public Task<byte[]> GetAsync(string key, CancellationToken ct = default) =>
        File.ReadAllBytesAsync(PathFor(key), ct);

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        File.Delete(PathFor(key));
        return Task.CompletedTask;
    }
}
