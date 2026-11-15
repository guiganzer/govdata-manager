namespace GovData.Downloads.Server;

public static class PublicationStateOwner
{
    public static FileStream Acquire(string storageRoot)
    {
        Directory.CreateDirectory(storageRoot);
        try
        {
            return new FileStream(Path.Combine(storageRoot, "publication-state.owner"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "Outro processo já controla o estado de organização neste armazenamento.", ex);
        }
    }
}
