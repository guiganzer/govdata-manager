namespace GovData.Downloads.Core;

public static class DownloadStateOwner
{
    public static FileStream Acquire(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        try
        {
            return new FileStream(Path.Combine(dataFolder, "download-state.owner"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "Outro processo já controla os downloads nesta pasta. Feche o servidor ou a interface antes de iniciar este processo.", ex);
        }
    }
}
