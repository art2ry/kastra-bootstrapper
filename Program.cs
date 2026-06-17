using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KastraPlayerLauncher
{
    static class Program
    {
        const string Protocol = "bbclient";
        const string BaseUrl = "https://kastra.lol";
        const string Version = "1.1.2";
        const string Repo = "art2ry/kastra-bootstrapper";

        static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kastra");
        static string VersionsDir => Path.Combine(Root, "Versions");
        static string InstalledExe => Path.Combine(Root, "KastraPlayerLauncher.exe");

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length == 0 || !args[0].StartsWith(Protocol + ":", StringComparison.OrdinalIgnoreCase))
            {
                Install(true);
                return;
            }

            Install(false);

            var form = new LauncherForm();
            var uri = args[0];
            form.Shown += async (s, e) => await Launch(uri, form);
            Application.Run(form);
        }

        static void Install(bool firstRun)
        {
            try
            {
                Directory.CreateDirectory(Root);

                var current = Process.GetCurrentProcess().MainModule.FileName;
                if (!string.Equals(current, InstalledExe, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Copy(current, InstalledExe, true); } catch { }
                }
                var exe = File.Exists(InstalledExe) ? InstalledExe : current;

                using (var key = Registry.CurrentUser.CreateSubKey("Software\\Classes\\" + Protocol))
                {
                    key.SetValue("", "URL:Kastra Player");
                    key.SetValue("URL Protocol", "");
                }
                using (var cmd = Registry.CurrentUser.CreateSubKey("Software\\Classes\\" + Protocol + "\\shell\\open\\command"))
                {
                    cmd.SetValue("", "\"" + exe + "\" \"%1\"");
                }

                if (firstRun)
                    MessageBox.Show("Kastra is installed. You can join games straight from kastra.lol now.", "Kastra", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                if (firstRun)
                    MessageBox.Show("Install failed: " + ex.Message, "Kastra", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static async Task Launch(string uri, LauncherForm form)
        {
            try
            {
                var q = ParseQuery(uri);
                q.TryGetValue("place", out var place);
                q.TryGetValue("ticket", out var ticket);
                q.TryGetValue("year", out var year);
                ticket ??= "";
                year ??= "2018";

                if (string.IsNullOrEmpty(place))
                    throw new Exception("No place was specified.");

                form.SetStatus("Connecting to Kastra");
                await SelfUpdate(form);

                string version;
                using (var http = NewClient(TimeSpan.FromSeconds(20)))
                {
                    var manifest = await http.GetStringAsync(BaseUrl + "/deploy/versions.json");
                    version = ReadManifestValue(manifest, year);
                }
                if (string.IsNullOrEmpty(version))
                    throw new Exception("There is no client for " + year + " yet.");

                var dir = Path.Combine(VersionsDir, version);
                if (!HasClient(dir))
                {
                    form.SetStatus("Installing");
                    await Download(BaseUrl + "/deploy/" + version + ".zip", dir, form);
                }
                else
                {
                    form.SetStatus("Updating");
                }

                form.SetStatus("Starting Kastra");
                Process.Start(new ProcessStartInfo
                {
                    FileName = FindClient(dir),
                    Arguments = JoinArgs(place, ticket),
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                });

                await Task.Delay(1500);
                Application.Exit();
            }
            catch (Exception ex)
            {
                form.SetStatus(ex.Message);
                await Task.Delay(5000);
                Application.Exit();
            }
        }

        static string JoinArgs(string place, string ticket)
        {
            // The patched RobloxPlayerBeta (Boost.program_options) accepts:
            //   authenticationUrl,a   authenticationTicket,t   joinScriptUrl,j
            // It does NOT take a "--play" verb and the 2014/2018 builds reject
            // "-b" (browserTrackerId is not a command-line option). Verified by
            // test-launch: -a/-t/-j parses cleanly and the client reaches the
            // PlaceLauncher request. Mirrors the reference launcher's argv.
            var auth = BaseUrl + "/Login/Negotiate.ashx";
            var join = BaseUrl + "/game/PlaceLauncher.ashx?placeId=" + place + "&ticket=" + Uri.EscapeDataString(ticket);
            return "-a \"" + auth + "\" -t \"" + ticket + "\" -j \"" + join + "\"";
        }

        static async Task Download(string url, string dir, LauncherForm form)
        {
            var zip = Path.Combine(Path.GetTempPath(), Path.GetFileName(url));
            Directory.CreateDirectory(VersionsDir);

            using (var http = NewClient(TimeSpan.FromMinutes(20)))
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1;
                using (var src = await resp.Content.ReadAsStreamAsync())
                using (var dst = File.Create(zip))
                {
                    var buffer = new byte[1 << 17];
                    long done = 0;
                    int read;
                    while ((read = await src.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await dst.WriteAsync(buffer, 0, read);
                        done += read;
                        if (total > 0) form.SetProgress((int)(done * 100 / total));
                    }
                }
            }

            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            ZipFile.ExtractToDirectory(zip, dir);
            try { File.Delete(zip); } catch { }
        }

        static async Task SelfUpdate(LauncherForm form)
        {
            try
            {
                string latest;
                using (var http = NewClient(TimeSpan.FromSeconds(6)))
                    latest = (await http.GetStringAsync("https://raw.githubusercontent.com/" + Repo + "/main/VERSION")).Trim();

                if (string.IsNullOrEmpty(latest) || latest == Version) return;

                form.SetStatus("Updating launcher");
                var temp = Path.Combine(Path.GetTempPath(), "KastraPlayerLauncher." + latest + ".exe");
                using (var http = NewClient(TimeSpan.FromMinutes(5)))
                    File.WriteAllBytes(temp, await http.GetByteArrayAsync("https://github.com/" + Repo + "/releases/latest/download/KastraPlayerLauncher.exe"));

                var self = Process.GetCurrentProcess().MainModule.FileName;
                var swap = Path.Combine(Path.GetTempPath(), "kastra_update.cmd");
                File.WriteAllText(swap,
                    "@echo off\r\n" +
                    "ping 127.0.0.1 -n 2 >nul\r\n" +
                    "copy /y \"" + temp + "\" \"" + self + "\" >nul\r\n" +
                    "copy /y \"" + self + "\" \"" + InstalledExe + "\" >nul\r\n" +
                    "start \"\" \"" + self + "\"\r\n");
                Process.Start(new ProcessStartInfo { FileName = swap, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = true });
                Application.Exit();
            }
            catch { }
        }

        static Dictionary<string, string> ParseQuery(string uri)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var q = uri.IndexOf('?');
            if (q < 0) return map;
            foreach (var part in uri.Substring(q + 1).Split('&'))
            {
                var eq = part.IndexOf('=');
                if (eq > 0) map[Uri.UnescapeDataString(part.Substring(0, eq))] = Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            return map;
        }

        static string ReadManifestValue(string json, string key)
        {
            var needle = "\"" + key + "\"";
            var i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i);
            if (i < 0) return null;
            var start = json.IndexOf('"', i) + 1;
            var end = json.IndexOf('"', start);
            return start > 0 && end > start ? json.Substring(start, end - start) : null;
        }

        static bool HasClient(string dir) => Directory.Exists(dir) && Directory.GetFiles(dir, "*.exe").Length > 0;

        static string FindClient(string dir)
        {
            foreach (var name in new[] { "RobloxPlayerBeta.exe", "RobloxApp.exe", "Roblox.exe" })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
            var any = Directory.GetFiles(dir, "*.exe");
            if (any.Length == 0) throw new Exception("Client executable not found.");
            return any[0];
        }

        static HttpClient NewClient(TimeSpan timeout) => new HttpClient { Timeout = timeout };
    }

    class LauncherForm : Form
    {
        readonly Label status;
        readonly ProgressBar bar;

        public LauncherForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(440, 190);
            BackColor = Color.FromArgb(18, 20, 26);
            ShowInTaskbar = true;
            Text = "Kastra";

            var title = new Label { Text = "KASTRA", ForeColor = Color.White, Font = new Font("Segoe UI", 30, FontStyle.Bold), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Height = 90 };
            status = new Label { Text = "Connecting", ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 10), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Height = 30 };
            var holder = new Panel { Dock = DockStyle.Top, Height = 22, Padding = new Padding(40, 8, 40, 0), BackColor = Color.Transparent };
            bar = new ProgressBar { Style = ProgressBarStyle.Marquee, Dock = DockStyle.Fill, MarqueeAnimationSpeed = 28 };

            holder.Controls.Add(bar);
            Controls.Add(holder);
            Controls.Add(status);
            Controls.Add(title);
        }

        public void SetStatus(string text)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text))); return; }
            status.Text = text;
        }

        public void SetProgress(int percent)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetProgress(percent))); return; }
            bar.Style = ProgressBarStyle.Continuous;
            bar.Value = Math.Max(0, Math.Min(100, percent));
        }
    }
}
