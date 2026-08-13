namespace Abraxius.Fabric;

public sealed record FabricIdentity(FabricId FabricId, FabricNodeId NodeId, string Schema = "abraxius.fabric.identity/1");

public static class FabricIdentityPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static FabricIdentity LoadOrCreate(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            var existing = JsonSerializer.Deserialize<FabricIdentity>(File.ReadAllText(fullPath));
            if (existing is not null && existing.FabricId.Value != Guid.Empty && existing.NodeId.Value != Guid.Empty) return existing;
            throw new InvalidDataException("Fabric identity file is malformed; refusing to silently replace node identity.");
        }

        var identity = new FabricIdentity(FabricId.New(), FabricNodeId.New());
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(identity, JsonOptions));
        File.Move(temporary, fullPath, false);
        return identity;
    }
}
