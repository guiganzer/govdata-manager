namespace GovData.Downloads.Core;

public sealed record StorageCapacity(string Volume, long AvailableBytes, long TotalBytes);

public sealed record StorageProtectionState(
    bool Blocked, string Volume, long AvailableBytes, long TotalBytes, string Message)
{
    public static StorageProtectionState Unknown { get; } = new(false, "", 0, 0, "");
    public double FreePercent => TotalBytes > 0 ? 100d * AvailableBytes / TotalBytes : 0;
}

public static class StorageProtection
{
    public const int MinimumFreePercent = 20;

    public static StorageCapacity ReadCapacity(string destination)
    {
        var full = Path.GetFullPath(destination);
        if (OperatingSystem.IsWindows())
        {
            var root = Path.GetPathRoot(full)
                ?? throw new IOException("Não foi possível identificar o volume de destino.");
            var drive = new DriveInfo(root);
            if (!drive.IsReady) throw new IOException($"O volume {root} não está disponível.");
            return new StorageCapacity(root, drive.AvailableFreeSpace, drive.TotalSize);
        }
        // Linux/macOS: GetPathRoot devolve sempre "/", o que media o disco do sistema e não o
        // volume montado (ex.: /mnt/dados). Usa o ponto de montagem mais longo que contém o
        // caminho real (com links simbólicos resolvidos).
        var real = RealPath(full);
        var mount = MountPointFor(real, MountPoints());
        var volume = new DriveInfo(mount);
        if (!volume.IsReady) throw new IOException($"O volume {mount} não está disponível.");
        return new StorageCapacity(mount, volume.AvailableFreeSpace, volume.TotalSize);
    }

    /// <summary>Ponto de montagem mais específico que contém <paramref name="path"/>.</summary>
    public static string MountPointFor(string path, IEnumerable<string> mountPoints)
    {
        string? best = null;
        foreach (var raw in mountPoints)
        {
            var mount = raw.Length > 1 ? raw.TrimEnd('/') : raw;
            var contains = mount == "/" || path == mount || path.StartsWith(mount + "/", StringComparison.Ordinal);
            if (contains && (best is null || mount.Length > best.Length)) best = mount;
        }
        return best ?? throw new IOException($"Nenhum volume montado contém {path}.");
    }

    private static IEnumerable<string> MountPoints()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            string name;
            try { name = drive.RootDirectory.FullName; } catch { continue; }
            yield return name;
        }
    }

    /// <summary>Caminho com links simbólicos resolvidos; partes ainda inexistentes ficam como estão.</summary>
    private static string RealPath(string full)
    {
        var current = "/";
        foreach (var part in full.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = current == "/" ? "/" + part : current + "/" + part;
            try
            {
                var info = new FileInfo(next);
                var target = info.Exists || Directory.Exists(next)
                    ? (info.LinkTarget is null ? null : info.ResolveLinkTarget(true)?.FullName)
                    : null;
                current = target is null ? next : Path.GetFullPath(target);
            }
            catch (IOException) { current = next; }
            catch (UnauthorizedAccessException) { current = next; }
        }
        return current;
    }

    public static StorageProtectionState Evaluate(StorageCapacity capacity)
    {
        if (capacity.TotalBytes <= 0 || capacity.AvailableBytes < 0)
            throw new IOException($"Capacidade inválida para o volume {capacity.Volume}.");
        var blocked = capacity.AvailableBytes <= capacity.TotalBytes / 5;
        var free = 100d * capacity.AvailableBytes / capacity.TotalBytes;
        var message = blocked
            ? $"Downloads pausados: o volume {capacity.Volume} tem {free:0.0}% de espaço livre. É necessário manter mais de 20% livre."
            : "";
        return new StorageProtectionState(blocked, capacity.Volume,
            capacity.AvailableBytes, capacity.TotalBytes, message);
    }
}
