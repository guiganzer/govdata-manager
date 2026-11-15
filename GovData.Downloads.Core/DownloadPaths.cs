namespace GovData.Downloads.Core;

public static class DownloadPaths
{
    public static string ForFile(string root, Orgao orgao, Funcao funcao, ArquivoCatalogo arquivo)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Escolha uma pasta de destino.", nameof(root));
        return Path.Combine(ForAgency(root, orgao), Segment(funcao.Nome), Segment(arquivo.NomeArquivo));
    }

    public static string ForAgency(string root, Orgao orgao)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Escolha uma pasta de destino.", nameof(root));
        return Path.Combine(Path.GetFullPath(root), Segment(orgao.Nome));
    }

    public static bool IsWithinAgency(string path, string agencyRoot)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(agencyRoot));
            var prefix = normalizedRoot + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Segment(string value)
    {
        var cleaned = string.Concat(value.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c));
        cleaned = cleaned.Trim().TrimEnd('.');
        if (cleaned.Length == 0 || cleaned is "." or "..") throw new ArgumentException("Nome de pasta ou arquivo inválido.");
        return cleaned;
    }
}
