using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Gestaobim.Ai;

/// <summary>Pastas do gateway de IA. <c>GBAI_HOME</c> redireciona tudo (testes).</summary>
public static class GbAiPaths
{
    private static string? Override => Environment.GetEnvironmentVariable("GBAI_HOME") is { Length: > 0 } v ? v : null;

    /// <summary>Contas OAuth e arquivos de execucao: local, fora de qualquer sincronizacao (OneDrive), acesso so do usuario.</summary>
    public static string DataDir => Override is { } o
        ? Path.Combine(o, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GestaoBIM", "gbai");

    public static string AuthDir => Path.Combine(DataDir, "auth");
    public static string RunDir => Path.Combine(DataDir, "run");

    /// <summary>Configuracoes do usuario (modelo por tarefa, chaves protegidas pela DPAPI).</summary>
    public static string SettingsDir => Override is { } o
        ? Path.Combine(o, "settings")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GestaoBIM", "Ai");

    public static string ProxyExe
    {
        get
        {
            if (Environment.GetEnvironmentVariable("GBAI_PROXY_EXE") is { Length: > 0 } custom) return custom;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GestaoBIM", "Tools", "cliproxyapi", "cli-proxy-api.exe");
        }
    }

    /// <summary>Cria a pasta e deixa so o usuario atual e o SYSTEM com acesso, sem heranca.</summary>
    public static void EnsurePrivate(string path)
    {
        var info = Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) return;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Usuario do Windows nao identificado.");
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
