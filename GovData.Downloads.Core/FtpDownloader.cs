using FluentFTP;
using FluentFTP.Exceptions;

namespace GovData.Downloads.Core;

public sealed class FtpDownloader
{
    public async Task DownloadAsync(DownloadJob job, Action<long, long?> report, CancellationToken cancellationToken,
        Func<string, Task>? onRestart = null, string? pinnedHost = null)
    {
        var uri = new Uri(job.Url);
        var remotePath = Uri.UnescapeDataString(uri.AbsolutePath);
        var partial = job.Destino + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(job.Destino)!);
        if (!job.PreservarAnterior && File.Exists(job.Destino) && !File.Exists(partial)) File.Move(job.Destino, partial);

        using var client = new AsyncFtpClient(pinnedHost ?? uri.Host, "anonymous", "anonymous@", uri.IsDefaultPort ? 21 : uri.Port);
        if (pinnedHost is not null) client.Config.DataConnectionType = FtpDataConnectionType.PASVEX;
        await client.Connect(cancellationToken);
        long remoteSize;
        try { remoteSize = await client.GetFileSize(remotePath, token: cancellationToken); }
        catch (FtpCommandException ex) when (ex.CompletionCode is "500" or "502" or "504") { remoteSize = -1; }
        long? total = remoteSize >= 0 ? remoteSize : null;
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (existing > 0 && total is long size && existing > size)
        {
            File.Delete(partial);
            existing = 0;
            if (onRestart is not null) await onRestart("Arquivo parcial maior que a fonte FTP; reiniciado do zero.");
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resume = existing > 0 && attempt == 0;
            report(resume ? existing : 0, total);
            var progress = new InlineProgress<FtpProgress>(_ =>
            {
                if (File.Exists(partial)) report(new FileInfo(partial).Length, total);
            });
            try
            {
                var result = await client.DownloadFile(partial, remotePath,
                    resume ? FtpLocalExists.Resume : FtpLocalExists.Overwrite,
                    FtpVerify.None, progress, cancellationToken);
                if (result != FtpStatus.Success)
                    throw new IOException($"Falha no download FTP: {result}.");
                var written = new FileInfo(partial).Length;
                if (total is long expected && written != expected)
                    throw new IOException($"Download FTP incompleto: {written} de {expected} bytes.");
                report(written, total ?? written);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (resume && ex is FtpException or IOException)
            {
                if (File.Exists(partial)) File.Delete(partial);
                existing = 0;
                if (onRestart is not null) await onRestart($"FTP não permitiu continuação; parcial apagado. {ex.Message}");
            }
        }
        throw new IOException("Não foi possível baixar o arquivo FTP.");
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
