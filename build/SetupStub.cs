// Instalador grafico .exe para pacotes grandes (o IExpress trava com ~0,7 GB). Compilado pelo csc do .NET Framework
// que ja vem no Windows (build/common.ps1, New-LargeSetupExe, /target:winexe). O .zip do pacote vai colado no fim
// deste .exe, seguido de um rodape:
//   [zip][nome curto do log: 32 bytes ASCII][SHA-256 do zip: 32 bytes][tamanho do zip: 8 bytes, little-endian]["GBSETUP2": 8 bytes]
// Ao rodar: abre janela grafica (sem CMD), detecta Revit 2025/2026/2027, recusa com o Revit aberto, confere o SHA-256,
// extrai para %TEMP% e roda o Install.ps1 oculto capturando a saida.
// Com /quiet (usado nos testes automaticos): roda silencioso sem abrir janela. Registro em %TEMP%\<slug>-setup.log.
// C# 5 (o csc do Windows): sem interpolacao de string nem operador ?.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Instalador Gestao BIM")]
[assembly: AssemblyProduct("Gestao BIM")]

static class SetupStub
{
    const string Magic = "GBSETUP2";
    const int TrailerSize = 32 + 32 + 8 + 8;
    internal static readonly int[] SupportedYears = new int[] { 2025, 2026, 2027 };

    static readonly object LogLock = new object();
    static string slug = "gb-setup";
    static string logFile;

    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [STAThread]
    static int Main(string[] args)
    {
        bool quiet = false;
        foreach (var a in args)
        {
            if (string.Equals(a, "/quiet", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "-quiet", StringComparison.OrdinalIgnoreCase))
            {
                quiet = true;
            }
        }

        string exe = Assembly.GetExecutingAssembly().Location;
        slug = ReadSlug(exe);
        logFile = Path.Combine(Path.GetTempPath(), slug + "-setup.log");

        if (quiet)
        {
            try
            {
                return RunInstall(exe, null, null, null);
            }
            catch (Exception ex)
            {
                Log("erro: " + ex);
                return 1;
            }
        }

        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var form = new SetupForm(exe, ExtractDisplayTitle(exe), logFile))
        {
            Application.Run(form);
            return form.ExitCode;
        }
    }

    static string ExtractDisplayTitle(string exe)
    {
        string name = Path.GetFileNameWithoutExtension(exe);
        var m = Regex.Match(name, @"(\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?)");
        if (m.Success) return "Gestao BIM " + m.Groups[1].Value;
        return "Gestao BIM";
    }

    internal static bool IsRevitInstalled(int year)
    {
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Autodesk\Revit\Autodesk Revit " + year))
            {
                if (k != null) return true;
            }
        }
        catch { }
        string addins = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Autodesk\Revit\Addins\" + year);
        return Directory.Exists(addins);
    }

    internal static bool IsRevitRunning()
    {
        try { return Process.GetProcessesByName("Revit").Length > 0; }
        catch { return false; }
    }

    internal static int RunInstall(
        string exe,
        int[] selectedYears,
        Action<int, string> onProgress,
        Action<string> onDetail)
    {
        Action<int, string> report = delegate(int pct, string msg)
        {
            if (onProgress != null) onProgress(pct, msg);
        };
        Action<string> detail = delegate(string line)
        {
            Log(line);
            if (onDetail != null) onDetail(line);
        };

        detail("Instalador iniciado: " + exe);
        if (IsRevitRunning())
        {
            detail("Revit aberto: instalacao recusada.");
            return 2;
        }

        string temp = Path.Combine(Path.GetTempPath(), slug + "-setup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(temp);
        string zip = Path.Combine(temp, "payload.zip");

        try
        {
        report(2, "Conferindo integridade do pacote (SHA-256)...");
        using (var input = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        {
            if (input.Length < TrailerSize)
                throw new InvalidDataException("O instalador esta incompleto.");

            var trailer = new byte[TrailerSize];
            input.Seek(-TrailerSize, SeekOrigin.End);
            ReadExactly(input, trailer, TrailerSize);
            if (Encoding.ASCII.GetString(trailer, 72, 8) != Magic)
                throw new InvalidDataException("O instalador nao contem o pacote (arquivo incompleto?).");

            long length = BitConverter.ToInt64(trailer, 64);
            long start = input.Length - TrailerSize - length;
            if (length <= 0 || start < 0)
                throw new InvalidDataException("O instalador esta corrompido (tamanho do pacote).");

            input.Seek(start, SeekOrigin.Begin);
            using (var sha = SHA256.Create())
            using (var output = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buffer = new byte[1 << 20];
                long readTotal = 0;
                long left = length;
                double totalMb = length / 1048576.0;
                int lastPct = -1;
                while (left > 0)
                {
                    int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (n <= 0) throw new EndOfStreamException("O instalador esta incompleto.");
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    output.Write(buffer, 0, n);
                    readTotal += n;
                    left -= n;
                    int pct = 2 + (int)((readTotal * 43L) / length);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        report(pct, string.Format("Conferindo integridade ({0:0} / {1:0} MB)...", readTotal / 1048576.0, totalMb));
                    }
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                for (int i = 0; i < 32; i++)
                {
                    if (sha.Hash[i] != trailer[32 + i])
                        throw new InvalidDataException("O instalador esta corrompido (SHA-256 nao confere). Baixe novamente.");
                }
            }
        }
        detail("Integridade SHA-256 conferida com sucesso.");

        report(46, "Extraindo arquivos do pacote...");
        string stage = Path.Combine(temp, "pacote");
        Directory.CreateDirectory(stage);
        string stageFull = Path.GetFullPath(stage);
        if (!stageFull.EndsWith(Path.DirectorySeparatorChar.ToString()))
            stageFull += Path.DirectorySeparatorChar;

        using (var archive = ZipFile.OpenRead(zip))
        {
            int totalEntries = Math.Max(archive.Entries.Count, 1);
            int done = 0;
            int lastPct = -1;
            foreach (var entry in archive.Entries)
            {
                string destPath = Path.GetFullPath(Path.Combine(stage, entry.FullName));
                if (!destPath.StartsWith(stageFull, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Entrada invalida no pacote: " + entry.FullName);

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destPath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                    entry.ExtractToFile(destPath, true);
                }
                done++;
                int pct = 46 + (done * 34) / totalEntries;
                if (pct != lastPct)
                {
                    lastPct = pct;
                    report(pct, string.Format("Extraindo arquivos ({0}/{1})...", done, totalEntries));
                }
            }
        }
        try { File.Delete(zip); } catch { }
        detail("Arquivos extraidos.");

        string install = Path.Combine(stage, "Install.ps1");
        if (!File.Exists(install))
            throw new FileNotFoundException("O pacote nao contem o Install.ps1.");

        report(82, "Instalando plugins e catalogo no Revit...");
        var psCmd = new StringBuilder();
        psCmd.Append("[Console]::OutputEncoding = [Text.Encoding]::UTF8; $OutputEncoding = [Text.Encoding]::UTF8; & '")
             .Append(install.Replace("'", "''")).Append("'");
        if (selectedYears != null && selectedYears.Length > 0)
        {
            psCmd.Append(" -Versions ");
            for (int i = 0; i < selectedYears.Length; i++)
            {
                if (i > 0) psCmd.Append(",");
                psCmd.Append(selectedYears[i]);
            }
        }
        string psArgs = "-NoProfile -ExecutionPolicy Bypass -Command \"" + psCmd.ToString().Replace("\"", "\\\"") + "\"";

        string psExe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var psi = new ProcessStartInfo(psExe, psArgs);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        int exitCode;
        using (var p = new Process())
        {
            p.StartInfo = psi;
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    detail(e.Data);
                    report(92, e.Data);
                }
            };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (!string.IsNullOrEmpty(e.Data))
                    detail("[erro] " + e.Data);
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            exitCode = p.ExitCode;
        }

        detail("Install.ps1 terminou com codigo " + exitCode);
        if (exitCode == 0) report(100, "Instalacao concluida com sucesso!");
        return exitCode;
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        }
    }

    static string ReadSlug(string exe)
    {
        try
        {
            using (var input = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length < TrailerSize) return "gb-setup";
                var trailer = new byte[TrailerSize];
                input.Seek(-TrailerSize, SeekOrigin.End);
                ReadExactly(input, trailer, TrailerSize);
                if (Encoding.ASCII.GetString(trailer, 72, 8) != Magic) return "gb-setup";
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

    internal static void Log(string text)
    {
        lock (LogLock)
        {
            try
            {
                File.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }
}

sealed class SetupForm : Form
{
    readonly string exePath;
    readonly string logPath;
    readonly Dictionary<int, CheckBox> yearChecks = new Dictionary<int, CheckBox>();
    readonly Panel warningPanel;
    readonly Label warningLabel;
    readonly Label statusLabel;
    readonly ProgressBar progressBar;
    readonly TextBox logBox;
    readonly Button btnInstall;
    readonly Button btnCancel;
    readonly Button btnDetails;
    readonly Button btnOpenLog;
    readonly Font statusFontRegular;
    readonly Font statusFontBold;

    bool isInstalling;
    bool isFinished;
    bool detailsExpanded;

    public int ExitCode { get; private set; }

    public SetupForm(string exePath, string titleText, string logPath)
    {
        this.exePath = exePath;
        this.logPath = logPath;
        this.ExitCode = 1;

        Text = titleText + " - Instalador";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        BackColor = Color.White;
        statusFontRegular = new Font("Segoe UI", 9.5f);
        statusFontBold = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        Font = statusFontRegular;
        ClientSize = new Size(560, 445);
        try { Icon = Icon.ExtractAssociatedIcon(exePath); } catch { }

        // Cabecalho
        var header = new Panel();
        header.Dock = DockStyle.Top;
        header.Height = 84;
        header.BackColor = Color.FromArgb(18, 38, 58);

        int textLeft = 24;
        try
        {
            var logoStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("gestaobim-logo.png");
            if (logoStream != null)
            {
                var pic = new PictureBox();
                pic.Image = Image.FromStream(logoStream);
                pic.SizeMode = PictureBoxSizeMode.Zoom;
                pic.Location = new Point(20, 14);
                pic.Size = new Size(56, 56);
                pic.BackColor = Color.Transparent;
                header.Controls.Add(pic);
                textLeft = 88;
            }
        }
        catch { }

        var lblTitle = new Label();
        lblTitle.Text = titleText;
        lblTitle.ForeColor = Color.White;
        lblTitle.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
        lblTitle.AutoSize = true;
        lblTitle.Location = new Point(textLeft, 16);

        var lblSub = new Label();
        lblSub.Text = "Plugins para Autodesk Revit 2025, 2026 e 2027 (sem precisar de administrador)";
        lblSub.ForeColor = Color.FromArgb(196, 214, 232);
        lblSub.Font = new Font("Segoe UI", 9.0f);
        lblSub.AutoSize = true;
        lblSub.Location = new Point(textLeft + 1, 50);

        header.Controls.Add(lblTitle);
        header.Controls.Add(lblSub);
        Controls.Add(header);

        // Conteudo principal
        int y = 98;

        var lblIncludes = new Label();
        lblIncludes.Text = "Componentes incluidos neste instalador:";
        lblIncludes.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        lblIncludes.ForeColor = Color.FromArgb(30, 41, 59);
        lblIncludes.AutoSize = true;
        lblIncludes.Location = new Point(24, y);
        Controls.Add(lblIncludes);
        y += 24;

        var lblList = new Label();
        lblList.Text =
            "  •  GB Material Studio, GB DWG Export, GB Cotas e GB Render\r\n" +
            "  •  GB Familias (catalogo completo + busca por IA local em CPU)";
        lblList.ForeColor = Color.FromArgb(71, 85, 105);
        lblList.AutoSize = true;
        lblList.Location = new Point(24, y);
        Controls.Add(lblList);
        y += 46;

        // Selecao de versoes do Revit
        var lblRevit = new Label();
        lblRevit.Text = "Instalar nas versoes do Revit:";
        lblRevit.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        lblRevit.ForeColor = Color.FromArgb(30, 41, 59);
        lblRevit.AutoSize = true;
        lblRevit.Location = new Point(24, y);
        Controls.Add(lblRevit);
        y += 26;

        int x = 28;
        int foundCount = 0;
        foreach (int year in SetupStub.SupportedYears)
        {
            bool installed = SetupStub.IsRevitInstalled(year);
            if (installed) foundCount++;
            var cb = new CheckBox();
            cb.Text = installed ? ("Revit " + year + " (detectado)") : ("Revit " + year);
            cb.Checked = installed;
            cb.AutoSize = true;
            cb.Location = new Point(x, y);
            cb.ForeColor = installed ? Color.FromArgb(15, 23, 42) : Color.FromArgb(100, 116, 139);
            Controls.Add(cb);
            yearChecks[year] = cb;
            x += 172;
        }
        y += 32;

        // Aviso de Revit aberto ou nao encontrado
        warningPanel = new Panel();
        warningPanel.Location = new Point(24, y);
        warningPanel.Size = new Size(512, 42);
        warningPanel.BackColor = Color.FromArgb(254, 243, 199);
        warningPanel.Visible = false;

        warningLabel = new Label();
        warningLabel.Dock = DockStyle.Fill;
        warningLabel.TextAlign = ContentAlignment.MiddleLeft;
        warningLabel.Padding = new Padding(10, 0, 10, 0);
        warningLabel.ForeColor = Color.FromArgb(146, 64, 14);
        warningLabel.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        warningPanel.Controls.Add(warningLabel);
        Controls.Add(warningPanel);
        y += 52;

        // Status e barra de progresso
        statusLabel = new Label();
        statusLabel.Text = foundCount > 0
            ? "Pronto para instalar. Clique em Instalar para comecar."
            : "Nenhum Revit 2025-2027 detectado automaticamente. Marque a versao desejada.";
        statusLabel.ForeColor = Color.FromArgb(51, 65, 85);
        statusLabel.Location = new Point(24, y);
        statusLabel.Size = new Size(512, 22);
        statusLabel.AutoEllipsis = true;
        Controls.Add(statusLabel);
        y += 24;

        progressBar = new ProgressBar();
        progressBar.Location = new Point(24, y);
        progressBar.Size = new Size(512, 22);
        progressBar.Minimum = 0;
        progressBar.Maximum = 100;
        progressBar.Value = 0;
        Controls.Add(progressBar);
        y += 34;

        // Painel de detalhes (colapsado inicialmente)
        logBox = new TextBox();
        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.Font = new Font("Consolas", 8.5f);
        logBox.BackColor = Color.FromArgb(248, 250, 252);
        logBox.ForeColor = Color.FromArgb(30, 41, 59);
        logBox.Location = new Point(24, 392);
        logBox.Size = new Size(512, 130);
        logBox.Visible = false;
        Controls.Add(logBox);

        // Rodape
        var footer = new Panel();
        footer.Dock = DockStyle.Bottom;
        footer.Height = 58;
        footer.BackColor = Color.FromArgb(248, 250, 252);

        var sep = new Panel();
        sep.Dock = DockStyle.Top;
        sep.Height = 1;
        sep.BackColor = Color.FromArgb(226, 232, 240);
        footer.Controls.Add(sep);

        btnDetails = new Button();
        btnDetails.Text = "Mostrar detalhes";
        btnDetails.FlatStyle = FlatStyle.Flat;
        btnDetails.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        btnDetails.BackColor = Color.White;
        btnDetails.Size = new Size(124, 32);
        btnDetails.Location = new Point(24, 14);
        btnDetails.Click += OnToggleDetails;
        footer.Controls.Add(btnDetails);

        btnOpenLog = new Button();
        btnOpenLog.Text = "Abrir log";
        btnOpenLog.FlatStyle = FlatStyle.Flat;
        btnOpenLog.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        btnOpenLog.BackColor = Color.White;
        btnOpenLog.Size = new Size(86, 32);
        btnOpenLog.Location = new Point(156, 14);
        btnOpenLog.Visible = false;
        btnOpenLog.Click += delegate
        {
            try
            {
                string notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
                if (File.Exists(logPath)) Process.Start(notepad, "\"" + logPath + "\"");
            }
            catch { }
        };
        footer.Controls.Add(btnOpenLog);

        btnCancel = new Button();
        btnCancel.Text = "Cancelar";
        btnCancel.FlatStyle = FlatStyle.Flat;
        btnCancel.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        btnCancel.BackColor = Color.White;
        btnCancel.Size = new Size(92, 32);
        btnCancel.Location = new Point(328, 14);
        btnCancel.Click += delegate
        {
            if (!isInstalling) Close();
        };
        footer.Controls.Add(btnCancel);

        btnInstall = new Button();
        btnInstall.Text = "Instalar";
        btnInstall.FlatStyle = FlatStyle.Flat;
        btnInstall.FlatAppearance.BorderSize = 0;
        btnInstall.BackColor = Color.FromArgb(22, 119, 255);
        btnInstall.ForeColor = Color.White;
        btnInstall.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        btnInstall.Size = new Size(108, 32);
        btnInstall.Location = new Point(428, 14);
        btnInstall.Click += OnInstallClick;
        footer.Controls.Add(btnInstall);

        Controls.Add(footer);

        RefreshRevitWarning();
    }

    void RefreshRevitWarning()
    {
        if (SetupStub.IsRevitRunning())
        {
            warningLabel.Text = "O Revit esta aberto. Salve e feche o Revit antes de clicar em Instalar.";
            warningPanel.Visible = true;
        }
        else
        {
            warningPanel.Visible = false;
        }
    }

    void OnToggleDetails(object sender, EventArgs e)
    {
        detailsExpanded = !detailsExpanded;
        logBox.Visible = detailsExpanded;
        btnDetails.Text = detailsExpanded ? "Ocultar detalhes" : "Mostrar detalhes";
        ClientSize = new Size(560, detailsExpanded ? 590 : 445);
    }

    void OnInstallClick(object sender, EventArgs e)
    {
        if (isFinished)
        {
            Close();
            return;
        }
        if (isInstalling) return;

        if (SetupStub.IsRevitRunning())
        {
            RefreshRevitWarning();
            MessageBox.Show(this,
                "O Revit esta aberto. Salve seus projetos, feche o Revit e clique em Instalar novamente.",
                "Revit aberto", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var years = new List<int>();
        foreach (var kv in yearChecks)
            if (kv.Value.Checked) years.Add(kv.Key);

        if (years.Count == 0)
        {
            MessageBox.Show(this,
                "Selecione pelo menos uma versao do Revit (2025, 2026 ou 2027).",
                "Nenhuma versao selecionada", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        warningPanel.Visible = false;
        isInstalling = true;
        statusLabel.ForeColor = Color.FromArgb(51, 65, 85);
        statusLabel.Font = statusFontRegular;
        btnInstall.Enabled = false;
        btnCancel.Enabled = false;
        foreach (var cb in yearChecks.Values) cb.Enabled = false;
        progressBar.Value = 0;
        logBox.Clear();

        int[] selected = years.ToArray();
        var t = new Thread(delegate()
        {
            int code = 1;
            string errorMsg = null;
            try
            {
                code = SetupStub.RunInstall(
                    exePath,
                    selected,
                    delegate(int pct, string msg) { UpdateUiProgress(pct, msg); },
                    delegate(string line) { AppendLogLine(line); });
            }
            catch (Exception ex)
            {
                SetupStub.Log("erro: " + ex);
                errorMsg = ex.Message;
                AppendLogLine("ERRO: " + ex.Message);
                code = 1;
            }
            FinishInstall(code, errorMsg);
        });
        t.IsBackground = true;
        t.Start();
    }

    void UpdateUiProgress(int pct, string msg)
    {
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<int, string>(UpdateUiProgress), pct, msg); } catch { }
            return;
        }
        if (pct < 0) pct = 0;
        if (pct > 100) pct = 100;
        progressBar.Value = pct;
        statusLabel.Text = msg;
    }

    void AppendLogLine(string line)
    {
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<string>(AppendLogLine), line); } catch { }
            return;
        }
        logBox.AppendText(line + Environment.NewLine);
    }

    void FinishInstall(int code, string errorMsg)
    {
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<int, string>(FinishInstall), code, errorMsg); } catch { }
            return;
        }
        isInstalling = false;
        ExitCode = code;
        btnOpenLog.Visible = File.Exists(logPath);

        if (code == 0)
        {
            isFinished = true;
            progressBar.Value = 100;
            statusLabel.ForeColor = Color.FromArgb(21, 128, 61);
            statusLabel.Font = statusFontBold;
            statusLabel.Text = "Instalacao concluida! Voce ja pode abrir o Revit.";
            btnInstall.Text = "Concluir";
            btnInstall.BackColor = Color.FromArgb(22, 163, 74);
            btnInstall.Enabled = true;
            btnCancel.Visible = false;
        }
        else if (code == 2)
        {
            RefreshRevitWarning();
            statusLabel.Text = "Feche o Revit e clique em Instalar novamente.";
            btnInstall.Enabled = true;
            btnCancel.Enabled = true;
            foreach (var cb in yearChecks.Values) cb.Enabled = true;
        }
        else
        {
            statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
            statusLabel.Font = statusFontBold;
            statusLabel.Text = "A instalacao falhou: " + (errorMsg ?? ("codigo " + code)) + " (veja o log).";
            if (!detailsExpanded) OnToggleDetails(this, EventArgs.Empty);
            btnInstall.Text = "Tentar de novo";
            btnInstall.Enabled = true;
            btnCancel.Text = "Fechar";
            btnCancel.Enabled = true;
            foreach (var cb in yearChecks.Values) cb.Enabled = true;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (isInstalling)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (statusFontBold != null) statusFontBold.Dispose();
            if (statusFontRegular != null) statusFontRegular.Dispose();
        }
        base.Dispose(disposing);
    }
}
