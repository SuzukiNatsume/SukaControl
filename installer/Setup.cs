using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class SetupWindow : Form
{
    readonly Button install = new Button(), cancel = new Button();
    readonly CheckBox startup = new CheckBox(), launch = new CheckBox();
    readonly ProgressBar progress = new ProgressBar();
    readonly Label status = new Label();
    bool running;
    static readonly int language = Language();
    static int Language() { string name = System.Globalization.CultureInfo.CurrentUICulture.Name; return name.StartsWith("ja") ? 3 : name.StartsWith("zh") ? (name.Contains("TW") || name.Contains("HK") || name.Contains("Hant") ? 1 : 0) : 2; }
    static string T(params string[] values) { return values[language]; }
    public SetupWindow()
    {
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Text = T("安装 SukaControl", "安裝 SukaControl", "Install SukaControl", "SukaControl のインストール"); ClientSize = new Size(540, 410); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(249, 249, 249); Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        Controls.Add(new Label { Text = "SukaControl", Font = new Font("Segoe UI", 25, FontStyle.Bold), AutoSize = true, Location = new Point(30, 25) });
        Controls.Add(new Label { Text = T("Redmi G Pro 2024 独立控制台", "Redmi G Pro 2024 獨立控制台", "Independent controls for Redmi G Pro 2024", "Redmi G Pro 2024 用の独立コントロール"), AutoSize = true, Location = new Point(32, 85) });
        Controls.Add(new Label { Text = T("安装后台服务时需要一次管理员确认。\n之后切换性能模式无需 UAC，不改变系统 UAC 设置。", "安裝背景服務時需要一次系統管理員確認。\n之後切換效能模式無須 UAC，不變更系統 UAC 設定。", "Approve administrator access once to install the service.\nEveryday controls require no UAC prompts or UAC changes.", "サービスのインストール時に一度だけ管理者権限を確認します。\n通常の操作では UAC の確認や設定変更は不要です。"), Size = new Size(480, 58), Location = new Point(32, 123) });
        Controls.Add(new TextBox { Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SukaControl"), ReadOnly = true, BorderStyle = BorderStyle.FixedSingle, Location = new Point(32, 193), Width = 476 });
        startup.Text = T("登录 Windows 时启动", "登入 Windows 時啟動", "Start when I sign in to Windows", "Windows へのサインイン時に起動"); startup.Checked = true; startup.SetBounds(32, 236, 475, 28); Controls.Add(startup);
        launch.Text = T("安装后打开 SukaControl", "安裝後開啟 SukaControl", "Open SukaControl after setup", "インストール後に SukaControl を開く"); launch.Checked = true; launch.SetBounds(32, 267, 475, 28); Controls.Add(launch);
        progress.SetBounds(32, 312, 476, 4); progress.Visible = false; Controls.Add(progress);
        status.SetBounds(32, 321, 476, 28); status.ForeColor = Color.DimGray; Controls.Add(status);
        install.Text = T("安装", "安裝", "Install", "インストール"); install.SetBounds(290, 359, 105, 34); install.Click += async delegate { await Install(); }; Controls.Add(install);
        cancel.Text = T("取消", "取消", "Cancel", "キャンセル"); cancel.SetBounds(405, 359, 103, 34); cancel.Click += delegate { Close(); }; Controls.Add(cancel);
        AcceptButton = install; CancelButton = cancel; FormClosing += delegate(object sender, FormClosingEventArgs e) { if (running) e.Cancel = true; };
    }
    async Task Install()
    {
        running = true; install.Enabled = false; cancel.Enabled = false; progress.Visible = true; progress.Style = ProgressBarStyle.Marquee; status.Text = T("正在准备安装…", "正在準備安裝…", "Preparing installation…", "インストールの準備中…");
        try
        {
            bool autostart = startup.Checked, open = launch.Checked;
            await Task.Run(delegate { InstallPayload(autostart, open); });
            running = false; Close();
        }
        catch (Exception ex)
        {
            status.Text = T("安装未完成。", "安裝未完成。", "Installation was not completed.", "インストールを完了できませんでした。"); MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); running = false; install.Enabled = true; cancel.Enabled = true; progress.Visible = false;
        }
    }
    internal static void InstallPayload(bool startup, bool launch)
    {
        string staging = Path.Combine(Path.GetTempPath(), "SukaControlSetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            using (ZipArchive zip = new ZipArchive(resource, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string path = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!path.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid installation archive.");
                    if (entry.Name.Length == 0) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)); using (var input = entry.Open()) using (var output = File.Create(path)) input.CopyTo(output);
                }
            }
            string owner = WindowsIdentity.GetCurrent().User.Value;
            using (Process process = Process.Start(new ProcessStartInfo(Path.Combine(staging, "SukaControl.exe"), "--install-service --owner-sid " + owner) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }))
            {
                process.WaitForExit(); if (process.ExitCode != 0) throw new IOException(T("后台服务安装失败。", "背景服務安裝失敗。", "The hardware service could not be installed.", "ハードウェア サービスをインストールできませんでした。"));
            }
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SukaControl", "SukaControl.exe");
            if (startup) using (var process = Process.Start(new ProcessStartInfo(installed, "--enable-startup-only") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })) process.WaitForExit();
            if (launch) Process.Start(new ProcessStartInfo(installed) { UseShellExecute = true });
        }
        finally
        {
            string verified = Path.GetFullPath(staging);
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!verified.StartsWith(parent + "SukaControlSetup-", StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(verified) + Path.DirectorySeparatorChar != parent) throw new IOException("Invalid cleanup location.");
            SafeDelete(verified);
        }
    }
    static void SafeDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        var root = new DirectoryInfo(path);
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0) { root.Delete(); return; }
        foreach (var child in root.GetFileSystemInfos()) { if (child is DirectoryInfo) SafeDelete(child.FullName); else child.Delete(); }
        root.Delete();
    }
    [STAThread] static void Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        if (Array.IndexOf(args, "/S") >= 0 || Array.IndexOf(args, "--silent") >= 0)
        {
            try { InstallPayload(false, false); } catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; } return;
        }
        Application.Run(new SetupWindow());
    }
}
