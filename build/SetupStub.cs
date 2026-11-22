// Instalador .exe para pacotes grandes (o IExpress trava com ~0,7 GB). Compilado pelo csc do .NET Framework que ja vem
// no Windows (build/common.ps1, New-LargeSetupExe). O .zip do pacote vai colado no fim deste .exe, seguido de um rodape:
//   [zip][nome curto do log: 32 bytes ASCII][SHA-256 do zip: 32 bytes][tamanho do zip: 8 bytes, little-endian]["GBSETUP2": 8 bytes]
// Ao rodar: recusa com o Revit aberto, confere o SHA-256, extrai para %TEMP% e roda o Install.ps1 do pacote.
// Sem pausa no fim com /quiet (usado nos testes automaticos). Registro em %TEMP%\<slug>-setup.log.
// C# 5 (o csc do Windows): sem interpolacao de string nem operador ?.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

[assembly: AssemblyTitle("Instalador Gestao BIM")]
[assembly: AssemblyProduct("Gestao BIM")]

static class SetupStub
{
    const string Magic = "GBSETUP2";
    const int TrailerSize = 32 + 32 + 8 + 8;
    static string slug = "gb-setup";
    static string logFile;

    static int Main(string[] args)
    {
        bool quiet = false;
        foreach (var a in args) if (a == "/quiet" || a == "-quiet") quiet = true;
        string exe = Assembly.GetExecutingAssembly().Location;
        string name = Path.GetFileNameWithoutExtension(exe);
        slug = ReadSlug(exe);
        logFile = Path.Combine(Path.GetTempPath(), slug + "-setup.log");
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = name + " - Instalador";
        int code = 1;
        try { code = Run(exe, args); }
        catch (Exception ex)
        {
            Log("erro: " + ex);
            Console.WriteLine();
            Console.WriteLine("A instalacao falhou: " + ex.Message);
            Console.WriteLine("Detalhes em " + logFile);
        }
        if (!quiet)
        {
            Console.WriteLine();
            Console.WriteLine("Pressione uma tecla para fechar.");
            try { Console.ReadKey(true); } catch { }
        }
        return code;
    }

    static int Run(string exe, string[] args)
    {
        Log("instalador iniciado: " + exe);
        if (Process.GetProcessesByName("Revit").Length > 0)
        {
            Log("revit aberto, instalacao recusada");
            Console.WriteLine("O Revit esta aberto. Salve, feche o Revit e execute este instalador de novo.");
            return 2;
        }

        string temp = Path.Combine(Path.GetTempPath(), slug + "-setup");
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);
        string zip = Path.Combine(temp, "payload.zip");

        Console.WriteLine("Conferindo o pacote...");
        using (var input = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        {
            if (input.Length < TrailerSize) throw new InvalidDataException("O instalador esta incompleto.");
            var trailer = new byte[TrailerSize];
            input.Seek(-TrailerSize, SeekOrigin.End);
            ReadExactly(input, trailer, TrailerSize);
            if (Encoding.ASCII.GetString(trailer, 72, 8) != Magic) throw new InvalidDataException("O instalador nao tem o pacote (arquivo incompleto?).");
            long length = BitConverter.ToInt64(trailer, 64);
            long start = input.Length - TrailerSize - length;
            if (length <= 0 || start < 0) throw new InvalidDataException("O instalador esta corrompido (tamanho do pacote).");
            input.Seek(start, SeekOrigin.Begin);
            using (var sha = SHA256.Create())
            using (var output = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buffer = new byte[1 << 20];
                long left = length;
                while (left > 0)
                {
                    int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (n <= 0) throw new EndOfStreamException("O instalador esta incompleto.");
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    output.Write(buffer, 0, n);
                    left -= n;
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                for (int i = 0; i < 32; i++)
                    if (sha.Hash[i] != trailer[32 + i]) throw new InvalidDataException("O instalador esta corrompido (SHA-256 nao confere). Baixe de novo.");
            }
        }
        Log("pacote conferido");

        Console.WriteLine("Extraindo...");
        string stage = Path.Combine(temp, "pacote");
        ZipFile.ExtractToDirectory(zip, stage);
        File.Delete(zip);
        string install = Path.Combine(stage, "Install.ps1");
        if (!File.Exists(install)) throw new FileNotFoundException("O pacote nao tem o Install.ps1.");

        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -File \"" + install + "\"");
        psi.UseShellExecute = false;
        using (var p = Process.Start(psi))
        {
            p.WaitForExit();
            Log("Install.ps1 terminou com codigo " + p.ExitCode);
            try { Directory.Delete(temp, true); } catch { }
            return p.ExitCode;
        }
    }

    // Nome curto (por exemplo "gb-familias") gravado no rodape; so letras, numeros e hifen (vira nome de arquivo).
    static string ReadSlug(string exe)
    {
        try
        {
            using (var input = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var trailer = new byte[TrailerSize];
                input.Seek(-TrailerSize, SeekOrigin.End);
                ReadExactly(input, trailer, TrailerSize);
                var sb = new StringBuilder();
                for (int i = 0; i < 32 && trailer[i] != 0; i++)
                {
                    char c = (char)trailer[i];
                    if (char.IsLetterOrDigit(c) || c == '-') sb.Append(c);
                }
                if (sb.Length > 0) return sb.ToString();
            }
        }
        catch { }
        return "gb-setup";
    }

    static void ReadExactly(Stream s, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buffer, read, count - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }

    static void Log(string text)
    {
        try { File.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine); } catch { }
    }
}
