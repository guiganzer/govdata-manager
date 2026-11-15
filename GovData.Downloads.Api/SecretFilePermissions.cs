using System.Security.AccessControl;
using System.Security.Principal;

namespace GovData.Downloads.Api;

public static class SecretFilePermissions
{
    public static void VerifyReadable(string path)
    {
        try
        {
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
            if (OperatingSystem.IsWindows())
            {
                if (!new FileInfo(path).GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected)
                    throw new IOException($"O arquivo de segredos {path} herda permissões da pasta. Restrinja a ACL do arquivo antes de iniciar a API.");
            }
            else if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
                throw new IOException($"O arquivo de segredos {path} permite acesso a outras contas.");
        }
        catch (UnauthorizedAccessException ex)
        {
            var account = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value : Environment.UserName;
            throw new IOException($"A conta {account} não consegue ler {path}. Conceda acesso de leitura a essa conta no arquivo de segredos e tente novamente.", ex);
        }
    }

    public static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return;
        }

        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Conta Windows sem SID.");
        var file = new FileInfo(path);
        try
        {
            var acl = file.GetAccessControl(AccessControlSections.Access);
            acl.SetAccessRuleProtection(true, false);
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                acl.RemoveAccessRuleSpecific(rule);

            foreach (var sid in new[]
            {
                user,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
            })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    AccessControlType.Allow));

            file.SetAccessControl(acl);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"A conta {user.Value} não consegue proteger {path}. " +
                "Conceda acesso total a essa conta no arquivo de segredos e tente novamente.", ex);
        }
    }
}
