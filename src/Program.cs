// dsh-launcher — DeepSeek Harness tray launcher (open-source edition)
// - Starts the DSH server (default: npx @deepseek-ai/dsh web) hidden, waits until
//   it is ready, opens the DeepSeek Harness UI, and stays in the system tray.
// - Watches the DSH plugin profile directory for plugin changes
//   (install/uninstall/enable/disable). A change is only confirmed after N seconds
//   without any new change (each new event refreshes the timer). Once confirmed,
//   waits until no session is running (all agent work done), then restarts the
//   server automatically. On restart the web UI windows are closed and reopened so
//   the new plugins load.
// - Tray menu: open UI / restart service / about / exit. Minimal notifications:
//   one when a plugin change is confirmed, one when the restart begins, plus errors.
// - Everything user-specific is configurable via config.json next to the exe
//   (see Config.Load). The Chrome PWA app-id is auto-detected when not configured.
// - Test-only environment overrides (documented in README):
//   DSH_MUTEX_SUFFIX - separate single-instance identity (e.g. "test")
//   DSH_WATCH_DIR    - plugin directory to watch instead of the configured one
//   DSH_NO_RESTART=1 - log instead of actually restarting
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using FormsTimer = System.Windows.Forms.Timer;

namespace DshLauncher
{
    static class Program
    {
        public const string Version = "1.0.1";
        public const string AppName = "DeepSeek Harness";

        internal static readonly string Suffix = Environment.GetEnvironmentVariable("DSH_MUTEX_SUFFIX") ?? "";
        static readonly string MutexName = "dsh_launcher_mutex" + Suffix;
        static readonly string EventName = "dsh_launcher_open" + Suffix;

        const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(uint dwProcessId);

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--version" || args[i] == "-v")
                {
                    // Print to the parent console when run from a terminal
                    // (a winexe has none of its own); fall back to a dialog.
                    if (AttachConsole(ATTACH_PARENT_PROCESS))
                    {
                        Console.WriteLine(AppName + " " + Version);
                        return;
                    }
                    MessageBox.Show(AppName + " " + Version,
                        "dsh-launcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // Another instance is already running: ask it to open the UI.
                    try { EventWaitHandle.OpenExisting(EventName).Set(); } catch { }
                    return;
                }
                using (var openEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName))
                {
                    Application.Run(new TrayContext(openEvent));
                }
            }
        }
    }

    // ------------------------------------------------------------------ config

    class Config
    {
        public int port = 3080;
        public string serverCommand = "npx @deepseek-ai/dsh web";
        public string workspace = "";          // server working dir; "" = exe dir
        public string profileDir = "";         // plugin watch dir; "" = ~/.dsh/profiles/web
        public string chromeAppId = "";        // "" = auto-detect the DSH Chrome PWA
        public string language = "zh";         // "zh" or "en"
        public string appName = "DeepSeek Harness";
        public bool openOnStart = true;
        public bool autoRestart = true;        // master switch for plugin-change auto restart
        public bool closeWebUisOnRestart = true;
        public bool watchdog = true;           // restart the server if it dies on its own
        public int changeQuietSeconds = 3;     // 3 s without new changes before confirming
        public int idlePollSeconds = 3;        // session-idle poll interval
        public int idleConfirmCount = 2;       // consecutive idle polls before restart
        public int stopTimeoutSeconds = 15;    // max wait for the old server to stop
        public int startTimeoutSeconds = 180;  // max wait for the new server to start
        public string logFile = "";            // "" = %TEMP%\dsh-launcher.log

        // Set when config.json could not be parsed (the user should be told).
        public static bool ParseFailed;
        public static string ParseError = "";

        public static Config Load()
        {
            var cfg = new Config();
            ParseFailed = false;
            ParseError = "";
            string path = ConfigPath();
            try
            {
                if (File.Exists(path))
                {
                    var ser = new JavaScriptSerializer();
                    var dict = ser.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                    if (dict != null)
                        Apply(cfg, dict);
                }
                else
                {
                    // First run: write a default config the user can edit.
                    try
                    {
                        string dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);
                        File.WriteAllText(path, ToJson(cfg));
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                ParseFailed = true;
                ParseError = ex.Message;
            }
            return cfg;
        }

        static string ConfigPath()
        {
            // Portable mode: a config.json sitting next to the exe wins, so the
            // whole thing can be carried around in one folder.
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            string local = Path.Combine(exeDir, "config.json");
            if (File.Exists(local))
                return local;
            // Standard mode: per-user AppData. The exe itself stays a single
            // clean file - it can live on the Desktop or anywhere else without
            // spawning extra files next to itself.
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "dsh-launcher", "config.json");
        }

        static void Apply(Config cfg, Dictionary<string, object> dict)
        {
            foreach (var kv in dict)
            {
                try
                {
                    switch (kv.Key)
                    {
                        case "port": cfg.port = Convert.ToInt32(kv.Value); break;
                        case "serverCommand": cfg.serverCommand = Convert.ToString(kv.Value); break;
                        case "workspace": cfg.workspace = Convert.ToString(kv.Value); break;
                        case "profileDir": cfg.profileDir = Convert.ToString(kv.Value); break;
                        case "chromeAppId": cfg.chromeAppId = Convert.ToString(kv.Value); break;
                        case "language": cfg.language = Convert.ToString(kv.Value); break;
                        case "appName": cfg.appName = Convert.ToString(kv.Value); break;
                        case "openOnStart": cfg.openOnStart = Convert.ToBoolean(kv.Value); break;
                        case "autoRestart": cfg.autoRestart = Convert.ToBoolean(kv.Value); break;
                        case "closeWebUisOnRestart": cfg.closeWebUisOnRestart = Convert.ToBoolean(kv.Value); break;
                        case "watchdog": cfg.watchdog = Convert.ToBoolean(kv.Value); break;
                        case "changeQuietSeconds": cfg.changeQuietSeconds = Convert.ToInt32(kv.Value); break;
                        case "idlePollSeconds": cfg.idlePollSeconds = Convert.ToInt32(kv.Value); break;
                        case "idleConfirmCount": cfg.idleConfirmCount = Convert.ToInt32(kv.Value); break;
                        case "stopTimeoutSeconds": cfg.stopTimeoutSeconds = Convert.ToInt32(kv.Value); break;
                        case "startTimeoutSeconds": cfg.startTimeoutSeconds = Convert.ToInt32(kv.Value); break;
                        case "logFile": cfg.logFile = Convert.ToString(kv.Value); break;
                    }
                }
                catch { }
            }
        }

        static string ToJson(Config cfg)
        {
            var ser = new JavaScriptSerializer();
            return ser.Serialize(cfg);
        }
    }

    // ------------------------------------------------------------------ i18n

    class Lang
    {
        readonly string current;

        public Lang(string language)
        {
            current = language == "en" ? "en" : "zh";
        }

        public string T(string key)
        {
            string s;
            if (current == "en")
                s = en.ContainsKey(key) ? en[key] : null;
            else
                s = zh.ContainsKey(key) ? zh[key] : null;
            return s ?? key;
        }

        static readonly Dictionary<string, string> zh = new Dictionary<string, string> {
            { "open_ui", "打开 DeepSeek Harness 界面" },
            { "restart", "重启服务" },
            { "about", "关于" },
            { "exit", "退出（停止服务）" },
            { "plugin_change", "检测到插件变动，将在聊天结束后自动重启服务" },
            { "restarting", "正在重启服务，稍候会自动重新打开界面" },
            { "fail_stop", "服务重启失败：无法停止旧服务" },
            { "fail_timeout", "服务重启失败：启动超时，请右键托盘图标手动重试" },
            { "fail_start", "启动服务失败：" },
            { "fail_open", "打开界面失败：" },
            { "startup_timeout", "服务启动超时，请检查网络后重试（可再次双击本程序重新打开界面）" },
            { "confirm_restart_busy", "有聊天工作正在进行，重启会中断它。确定要现在重启服务吗？" },
            { "watchdog_restart", "服务异常退出，正在自动重启" },
            { "config_failed", "config.json 解析失败，已使用默认配置。\n错误信息：{0}" },
            { "about_text", "DeepSeek Harness 托盘启动器\n版本 {0}\n\n自动启动服务、监控插件变动、聊天结束后自动重启。" },
        };

        static readonly Dictionary<string, string> en = new Dictionary<string, string> {
            { "open_ui", "Open DeepSeek Harness" },
            { "restart", "Restart Service" },
            { "about", "About" },
            { "exit", "Exit (Stop Service)" },
            { "plugin_change", "Plugin change detected; the service will restart after chat work finishes" },
            { "restarting", "Restarting the service, the UI will reopen shortly" },
            { "fail_stop", "Restart failed: could not stop the old service" },
            { "fail_timeout", "Restart failed: startup timeout, retry from the tray menu" },
            { "fail_start", "Failed to start the service: " },
            { "fail_open", "Failed to open the UI: " },
            { "startup_timeout", "Service startup timed out; check your network and double-click this app again" },
            { "confirm_restart_busy", "Chat work is in progress and will be interrupted. Restart the service anyway?" },
            { "watchdog_restart", "The service stopped unexpectedly; restarting automatically" },
            { "config_failed", "config.json could not be parsed; using defaults.\nError: {0}" },
            { "about_text", "DeepSeek Harness tray launcher\nVersion {0}\n\nStarts the service, watches plugin changes and restarts automatically after chat work finishes." },
        };
    }

    // ------------------------------------------------------------------ tray app

    class TrayContext : ApplicationContext
    {
        readonly Config cfg;
        readonly Lang lang;
        readonly string url;
        readonly string workspace;
        readonly string watchDir;
        readonly string logPath;
        readonly bool noRestart;
        readonly string appId;   // detected or configured Chrome PWA app-id ("" = none)

        NotifyIcon icon;
        ContextMenuStrip menu;

        // theme
        bool darkMode;
        int themeCheckTicks;

        Process serverProcess;
        bool weStartedServer;
        FormsTimer readyTimer;
        int waitTicks;

        // plugin watch
        FileSystemWatcher watcher;
        bool pluginEventSeen;
        long lastPluginEventTicks;
        long lastFsLogTicks;
        bool pendingRestart;
        DateTime restartCooldownUntilUtc;

        // restart machine
        int restartPhase;            // 0 idle, 1 wait-port-down, 2 wait-ready
        DateTime phaseDeadline;
        int idlePollTicks;
        int idleStreak;

        // background poller results (updated off the UI thread so the tray never
        // blocks on network / WMI / netstat calls)
        volatile bool serverUp;
        volatile bool busy;
        Thread pollThread;
        volatile bool pollRunning;
        bool everUp;                       // saw the server up at least once
        DateTime lastServerUpUtc = DateTime.MinValue;

        public TrayContext(EventWaitHandle openEvent)
        {
            cfg = Config.Load();
            lang = new Lang(cfg.language);
            if (Config.ParseFailed)
            {
                MessageBox.Show(string.Format(lang.T("config_failed"), Config.ParseError),
                    cfg.appName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            url = "http://127.0.0.1:" + cfg.port;
            workspace = cfg.workspace.Length > 0
                ? cfg.workspace
                : Path.GetDirectoryName(Application.ExecutablePath);
            watchDir = Environment.GetEnvironmentVariable("DSH_WATCH_DIR")
                ?? (cfg.profileDir.Length > 0 ? ExpandHome(cfg.profileDir)
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".dsh", "profiles", "web"));
            logPath = cfg.logFile.Length > 0 ? cfg.logFile
                : Path.Combine(Path.GetTempPath(), "dsh-launcher" + Program.Suffix + ".log");
            noRestart = Environment.GetEnvironmentVariable("DSH_NO_RESTART") == "1";
            appId = cfg.chromeAppId.Length > 0 ? cfg.chromeAppId : DetectChromeAppId();
            Log("--- " + cfg.appName + " " + Program.Version + " start, port=" + cfg.port
                + ", watch=" + watchDir + ", appId=" + (appId.Length > 0 ? appId : "(none)")
                + ", lang=" + cfg.language + ", noRestart=" + noRestart);

            icon = new NotifyIcon();
            icon.Icon = LoadAppIcon();
            icon.Text = cfg.appName;
            icon.Visible = true;
            icon.DoubleClick += delegate { OpenApp(); };

            menu = new ContextMenuStrip();
            menu.Font = SystemFonts.MenuFont;
            menu.ShowImageMargin = false;
            menu.Items.Add(lang.T("open_ui"), null, delegate { OpenApp(); });
            menu.Items.Add(lang.T("restart"), null, delegate { MenuRestart(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(lang.T("about"), null, delegate { ShowAbout(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(lang.T("exit"), null, delegate { ExitApp(); });
            icon.ContextMenuStrip = menu;
            ApplyTheme(IsSystemDarkMode());

            // React to a second launch by re-opening the UI.
            var signalTimer = new FormsTimer();
            signalTimer.Interval = 500;
            signalTimer.Tick += delegate { if (openEvent.WaitOne(0)) OpenApp(); };
            signalTimer.Start();

            if (cfg.autoRestart)
                SetupWatcher();
            else
                Log("autoRestart disabled, plugin watch skipped");

            var house = new FormsTimer();
            house.Interval = 1000;
            house.Tick += HouseTick;
            house.Start();

            StartPoller();

            serverUp = IsServerUp();
            if (serverUp)
            {
                everUp = true;
                lastServerUpUtc = DateTime.UtcNow;
                if (cfg.openOnStart)
                    OpenApp();
            }
            else
            {
                StartServer();
                readyTimer = new FormsTimer();
                readyTimer.Interval = 2000;
                readyTimer.Tick += CheckReady;
                readyTimer.Start();
            }
            Log("init done, serverUp=" + serverUp);
        }

        // Background thread that keeps serverUp / busy fresh so the UI thread
        // never blocks on network or process scans.
        void StartPoller()
        {
            pollRunning = true;
            pollThread = new Thread(PollLoop);
            pollThread.IsBackground = true;
            pollThread.Name = "dsh-poller";
            pollThread.Start();
        }

        void PollLoop()
        {
            int tick = 0;
            while (pollRunning)
            {
                try
                {
                    serverUp = IsServerUp();
                    if (serverUp)
                    {
                        everUp = true;
                        lastServerUpUtc = DateTime.UtcNow;
                    }
                    tick++;
                    if (tick * 1000 >= cfg.idlePollSeconds * 1000)
                    {
                        tick = 0;
                        busy = AnySessionRunning();
                    }
                }
                catch { }
                Thread.Sleep(1000);
            }
        }

        // ---------------------------------------------------------------- housekeeping

        void HouseTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;

            // 0) watchdog: the server was up but has now been down for a while and
            //    it is not because of us - bring it back
            if (cfg.watchdog && everUp && !serverUp && restartPhase == 0
                && !pendingRestart && readyTimer == null
                && now.Subtract(lastServerUpUtc).TotalSeconds > 12)
            {
                Log("Watchdog: server down -> auto restart");
                Balloon(lang.T("watchdog_restart"));
                BeginRestart();
            }

            // 1) plugin change: confirmed only after changeQuietSeconds without any
            //    new change (each new event refreshes the timer)
            if (pluginEventSeen && restartPhase == 0 && now >= restartCooldownUntilUtc)
            {
                if (now.Subtract(new DateTime(Interlocked.Read(ref lastPluginEventTicks))).TotalSeconds
                    >= cfg.changeQuietSeconds)
                {
                    pluginEventSeen = false;
                    if (!pendingRestart)
                    {
                        pendingRestart = true;
                        Log("Plugin change confirmed -> pending restart");
                        Balloon(lang.T("plugin_change"));
                    }
                }
            }

            // 2) idle wait (uses the background poller's cached busy result)
            idlePollTicks++;
            if (idlePollTicks * 1000 >= cfg.idlePollSeconds * 1000)
            {
                idlePollTicks = 0;
                if (pendingRestart && restartPhase == 0)
                {
                    if (busy)
                        idleStreak = 0;
                    else
                    {
                        idleStreak++;
                        if (idleStreak >= cfg.idleConfirmCount)
                        {
                            idleStreak = 0;
                            pendingRestart = false;
                            Log("Idle confirmed -> auto restart");
                            BeginRestart();
                        }
                    }
                }
            }

            // 3) restart machine (uses the cached serverUp value)
            if (restartPhase == 1)
            {
                if (!serverUp)
                {
                    Log("Old server stopped -> starting new one");
                    StartServer();
                    restartPhase = 2;
                    phaseDeadline = now.AddSeconds(cfg.startTimeoutSeconds);
                }
                else if (now > phaseDeadline)
                {
                    restartPhase = 0;
                    Log("Restart failed: old server did not stop");
                    Balloon(lang.T("fail_stop"), ToolTipIcon.Error);
                }
            }
            else if (restartPhase == 2)
            {
                if (serverUp)
                {
                    restartPhase = 0;
                    restartCooldownUntilUtc = now.AddSeconds(60);
                    pluginEventSeen = false;   // clear events captured during the restart
                    Log("Restart complete");
                    OpenApp();   // the web UIs were closed during the restart: reopen fresh
                }
                else if (now > phaseDeadline)
                {
                    restartPhase = 0;
                    Log("Restart failed: startup timeout");
                    Balloon(lang.T("fail_timeout"), ToolTipIcon.Error);
                }
            }

            // 4) tray tooltip state + follow system light/dark mode
            string suffix = restartPhase != 0 ? " *" : pendingRestart ? " (pending)" : "";
            icon.Text = cfg.appName + suffix;

            if (++themeCheckTicks >= 5)
            {
                themeCheckTicks = 0;
                bool dark = IsSystemDarkMode();
                if (dark != darkMode)
                    ApplyTheme(dark);
            }
        }

        void CheckReady(object sender, EventArgs e)
        {
            if (serverUp)
            {
                readyTimer.Stop();
                if (cfg.openOnStart)
                    OpenApp();
            }
            else if (++waitTicks > cfg.startTimeoutSeconds / 2)
            {
                readyTimer.Stop();
                Balloon(lang.T("startup_timeout"), ToolTipIcon.Warning);
            }
        }

        // ---------------------------------------------------------------- plugin watch

        void SetupWatcher()
        {
            try
            {
                if (!Directory.Exists(watchDir))
                {
                    Log("watch dir missing: " + watchDir);
                    return;
                }
                watcher = new FileSystemWatcher(watchDir);
                watcher.IncludeSubdirectories = true;
                watcher.InternalBufferSize = 65536;
                watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                    | NotifyFilters.DirectoryName | NotifyFilters.Size;
                watcher.Changed += OnPluginFsEvent;
                watcher.Created += OnPluginFsEvent;
                watcher.Deleted += OnPluginFsEvent;
                watcher.Renamed += OnPluginFsEvent;
                watcher.Error += delegate { Log("FSW error event"); };
                watcher.EnableRaisingEvents = true;
                Log("Watcher active");
            }
            catch (Exception ex)
            {
                Log("Watcher setup failed: " + ex.Message);
            }
        }

        void OnPluginFsEvent(object sender, FileSystemEventArgs e)
        {
            // cordis.yml is the server-composed plugin-tree root: the server rewrites
            // it on every startup. It is NOT a user plugin change, so ignore it.
            if (string.Equals(e.FullPath, Path.Combine(watchDir, "cordis.yml"),
                    StringComparison.OrdinalIgnoreCase))
                return;

            // Atomic-write temp files ("_tmp_<pid>_<rand>"): the renamed final file
            // fires its own event, so the temp name alone must not count.
            string name = Path.GetFileName(e.FullPath);
            if (name != null && name.StartsWith("_tmp_", StringComparison.OrdinalIgnoreCase))
                return;

            // Only real plugin changes count:
            //  - top-level manifest files (package.json, pnpm-lock.yaml,
            //    pnpm-workspace.yaml, cordis.patch.yml);
            //  - the node_modules package directory itself being created/deleted
            //    (node_modules\pkg or node_modules\@scope\pkg).
            // Anything deeper (files inside installed packages, caches, runtime
            // state) is ignored.
            string rel = e.FullPath;
            if (e.FullPath.StartsWith(watchDir, StringComparison.OrdinalIgnoreCase))
                rel = e.FullPath.Substring(watchDir.Length).TrimStart('\\', '/');
            string[] parts = rel.Split(new char[] { '\\', '/' });
            if (parts.Length == 1)
            {
                bool manifest = name == "package.json" || name == "pnpm-lock.yaml"
                    || name == "pnpm-workspace.yaml" || name == "cordis.patch.yml";
                if (!manifest)
                    return;
            }
            else if (parts.Length >= 2 && parts[0].Equals("node_modules", StringComparison.OrdinalIgnoreCase))
            {
                if (!(e.ChangeType == WatcherChangeTypes.Created || e.ChangeType == WatcherChangeTypes.Deleted))
                    return;
                if (parts.Length != 2 && !(parts.Length == 3 && parts[1].StartsWith("@")))
                    return;
            }
            else
            {
                return;
            }

            // During our own restart, or shortly after we started a server, profile
            // writes are startup noise, not user plugin changes - drop them.
            if (restartPhase != 0 || DateTime.UtcNow < restartCooldownUntilUtc)
                return;

            // Throttled per-event log for diagnosis.
            long nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks - lastFsLogTicks > TimeSpan.FromSeconds(2).Ticks)
            {
                lastFsLogTicks = nowTicks;
                Log("FSW: " + e.ChangeType + " " + e.FullPath);
            }
            pluginEventSeen = true;
            Interlocked.Exchange(ref lastPluginEventTicks, nowTicks);
        }

        // ---------------------------------------------------------------- restart

        void MenuRestart()
        {
            if (restartPhase != 0) return;
            // Don't kill running work on a stray click.
            if (busy)
            {
                DialogResult r = MessageBox.Show(lang.T("confirm_restart_busy"),
                    cfg.appName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes)
                    return;
            }
            pendingRestart = false;
            Log("Manual restart requested");
            BeginRestart();
        }

        void BeginRestart()
        {
            if (restartPhase != 0) return;
            if (noRestart)
            {
                Log("Restart suppressed (test mode)");
                return;
            }
            Log("BeginRestart");
            // Tell the user first: the web UIs are about to close and the screen
            // will go blank for a few seconds - without this it looks frozen.
            Balloon(lang.T("restarting"));
            restartPhase = 1;
            phaseDeadline = DateTime.UtcNow.AddSeconds(cfg.stopTimeoutSeconds);
            // The WMI/netstat scans and taskkill are slow: run them off the UI
            // thread. The restart machine above waits for serverUp to drop.
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (cfg.closeWebUisOnRestart)
                        CloseWebUIs();
                    KillServer();
                }
                catch (Exception ex)
                {
                    Log("restart task error: " + ex.Message);
                }
            });
        }

        // Close the DeepSeek Harness web windows so the reopened page loads the
        // new plugins. Uses the Chrome app-id when known; otherwise falls back to
        // closing chrome processes connected to the DSH port.
        void CloseWebUIs()
        {
            var pids = new List<int>();
            if (appId.Length > 0)
            {
                try
                {
                    using (var searcher = new System.Management.ManagementObjectSearcher(
                        "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='chrome.exe'"))
                    {
                        foreach (var obj in searcher.Get())
                        {
                            string cmd = Convert.ToString(obj["CommandLine"]);
                            if (cmd != null && cmd.IndexOf("--app-id=" + appId,
                                    StringComparison.OrdinalIgnoreCase) >= 0)
                                pids.Add(Convert.ToInt32(obj["ProcessId"]));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("WMI chrome scan failed: " + ex.Message);
                }
            }
            if (pids.Count == 0)
            {
                try
                {
                    var psi = new ProcessStartInfo("netstat.exe", "-ano");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    using (var p = Process.Start(psi))
                    {
                        string output = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(5000);
                        foreach (string line in output.Split('\n'))
                        {
                            // Client side of a connection to our port: the FOREIGN
                            // address port must equal cfg.port exactly (never a
                            // substring, so :30800 can't match :3080).
                            if (!line.Contains("ESTABLISHED"))
                                continue;
                            if (PortOfColumn(line, 2) != cfg.port)
                                continue;
                            string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            int pid;
                            if (parts.Length > 0 && int.TryParse(parts[parts.Length - 1], out pid) && pid > 0)
                            {
                                try
                                {
                                    var proc = Process.GetProcessById(pid);
                                    if (proc != null &&
                                        proc.ProcessName.ToLowerInvariant().Contains("chrome"))
                                        pids.Add(pid);
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("netstat chrome scan failed: " + ex.Message);
                }
            }
            foreach (int pid in pids)
            {
                try
                {
                    var psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    Process.Start(psi);
                    Log("Closed web UI pid " + pid);
                }
                catch (Exception ex)
                {
                    Log("Close web UI failed: " + ex.Message);
                }
            }
        }

        void KillServer()
        {
            int pid = -1;
            if (serverProcess != null)
            {
                try { if (!serverProcess.HasExited) pid = serverProcess.Id; } catch { }
            }
            if (pid < 0)
                pid = FindPortOwnerPid();
            if (pid > 0)
            {
                try
                {
                    var psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    Process.Start(psi);
                    Log("taskkill pid " + pid);
                }
                catch (Exception ex)
                {
                    Log("taskkill failed: " + ex.Message);
                }
            }
            serverProcess = null;
            weStartedServer = false;
        }

        int FindPortOwnerPid()
        {
            try
            {
                var psi = new ProcessStartInfo("netstat.exe", "-ano");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    foreach (string line in output.Split('\n'))
                    {
                        // The LISTENING socket's LOCAL address port must equal
                        // cfg.port exactly (never a substring).
                        if (!line.Contains("LISTENING"))
                            continue;
                        if (PortOfColumn(line, 1) != cfg.port)
                            continue;
                        string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        int pid;
                        if (parts.Length > 0 && int.TryParse(parts[parts.Length - 1], out pid))
                            return pid;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("netstat failed: " + ex.Message);
            }
            return -1;
        }

        // Port of the given netstat address column (1 = local, 2 = foreign).
        // Parses the real address token so :3080 never matches :30800.
        static int PortOfColumn(string line, int column)
        {
            string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= column)
                return -1;
            string addr = parts[column];
            int idx = addr.LastIndexOf(':');
            if (idx < 0 || idx == addr.Length - 1)
                return -1;
            int port;
            return int.TryParse(addr.Substring(idx + 1), out port) ? port : -1;
        }

        // ---------------------------------------------------------------- server

        void StartServer()
        {
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c " + cfg.serverCommand;
                psi.WorkingDirectory = workspace;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                serverProcess = Process.Start(psi);
                weStartedServer = true;
                // A fresh server rewrites profile files at startup; don't treat
                // those as plugin changes.
                restartCooldownUntilUtc = DateTime.UtcNow.AddSeconds(60);
                Log("Server started (pid " + serverProcess.Id + ")");
            }
            catch (Exception ex)
            {
                Log("StartServer failed: " + ex.Message);
                Balloon(lang.T("fail_start") + ex.Message, ToolTipIcon.Error);
            }
        }

        bool IsServerUp()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 1500;
                using (var resp = (HttpWebResponse)req.GetResponse()) { }
                return true;
            }
            catch
            {
                return false;
            }
        }

        bool AnySessionRunning()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url + "/api/session.list");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 5000;
                string body = "{\"type\":\"client-request\",\"rpcId\":\""
                    + Guid.NewGuid().ToString("N")
                    + "\",\"method\":\"session.list\",\"payload\":{}}";
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream())
                    s.Write(bytes, 0, bytes.Length);
                string json;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    json = reader.ReadToEnd();
                var ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                var root = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) return false;
                var result = root["result"] as Dictionary<string, object>;
                if (result == null || !(bool)result["ok"]) return false;
                var value = result["value"] as Dictionary<string, object>;
                if (value == null || !value.ContainsKey("items")) return false;
                var items = value["items"] as object[];
                if (items == null) return false;
                foreach (var it in items)
                {
                    var d = it as Dictionary<string, object>;
                    if (d != null && d.ContainsKey("running") && (bool)d["running"])
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- ui open / exit

        void OpenApp()
        {
            try
            {
                if (appId.Length > 0)
                {
                    string internalLnk = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Google\Chrome\User Data\Default\Web Applications\_crx_"
                        + appId + @"\DeepSeek Harness.lnk");
                    if (!File.Exists(internalLnk))
                    {
                        string dir = Path.GetDirectoryName(internalLnk);
                        if (Directory.Exists(dir))
                        {
                            string[] lnks = Directory.GetFiles(dir, "*.lnk");
                            if (lnks.Length > 0)
                                internalLnk = lnks[0];
                        }
                    }
                    if (File.Exists(internalLnk))
                    {
                        Process.Start(internalLnk);
                        return;
                    }
                    string chrome = FindChrome();
                    if (chrome != null)
                    {
                        Process.Start(chrome, "--profile-directory=Default --app-id=" + appId);
                        return;
                    }
                }
                Process.Start(url);
            }
            catch (Exception ex)
            {
                Balloon(lang.T("fail_open") + ex.Message, ToolTipIcon.Error);
            }
        }

        void ShowAbout()
        {
            MessageBox.Show(string.Format(lang.T("about_text"), Program.Version),
                cfg.appName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static string FindChrome()
        {
            string[] candidates = new string[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Google\Chrome\Application\chrome.exe"),
            };
            foreach (string c in candidates)
            {
                if (File.Exists(c))
                    return c;
            }
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"))
                {
                    if (key != null)
                    {
                        string p = key.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(p) && File.Exists(p))
                            return p;
                    }
                }
            }
            catch { }
            return null;
        }

        // Find the DSH Chrome PWA app-id by scanning Chrome's installed web apps
        // for one that contains the DeepSeek Harness icon. Works without config.
        static string DetectChromeAppId()
        {
            try
            {
                string webApps = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Google\Chrome\User Data\Default\Web Applications");
                if (!Directory.Exists(webApps))
                    return "";
                foreach (string dir in Directory.GetDirectories(webApps, "_crx_*"))
                {
                    if (File.Exists(Path.Combine(dir, "DeepSeek Harness.ico")))
                        return Path.GetFileName(dir).Substring("_crx_".Length);
                }
            }
            catch { }
            return "";
        }

        void ExitApp()
        {
            Log("Exit requested");
            if (weStartedServer && serverProcess != null)
            {
                try
                {
                    var psi = new ProcessStartInfo("taskkill.exe",
                        "/PID " + serverProcess.Id + " /T /F");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    Process.Start(psi);
                    serverProcess.WaitForExit(3000);
                }
                catch { }
            }
            if (readyTimer != null)
                readyTimer.Stop();
            pollRunning = false;   // stop the background poller
            if (watcher != null)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
            ExitThread();
        }

        void Balloon(string text)
        {
            Balloon(text, ToolTipIcon.Info);
        }

        void Balloon(string text, ToolTipIcon tipIcon)
        {
            icon.ShowBalloonTip(4000, cfg.appName, text, tipIcon);
        }

        // ---------------------------------------------------------------- theme

        static bool IsSystemDarkMode()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key == null) return false;
                    object value = key.GetValue("AppsUseLightTheme", 1);
                    if (value is int)
                        return (int)value == 0;
                }
            }
            catch { }
            return false;
        }

        void ApplyTheme(bool dark)
        {
            darkMode = dark;
            menu.Renderer = new ToolStripProfessionalRenderer(new ThemeColorTable(dark));
            Color text = dark ? Color.FromArgb(243, 243, 243) : Color.Black;
            foreach (ToolStripItem item in menu.Items)
            {
                ToolStripMenuItem mi = item as ToolStripMenuItem;
                if (mi != null)
                    mi.ForeColor = text;
            }
        }

        Icon LoadAppIcon()
        {
            if (appId.Length > 0)
            {
                try
                {
                    string ico = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Google\Chrome\User Data\Default\Web Applications\_crx_"
                        + appId + @"\DeepSeek Harness.ico");
                    if (File.Exists(ico))
                        return new Icon(ico);
                }
                catch { }
            }
            try
            {
                Icon exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (exeIcon != null)
                    return exeIcon;
            }
            catch { }
            return SystemIcons.Application;
        }

        void Log(string message)
        {
            try
            {
                // Simple rotation: keep the log under 1 MB, park the old one as .old.
                const long maxBytes = 1024 * 1024;
                var fi = new FileInfo(logPath);
                if (fi.Exists && fi.Length > maxBytes)
                {
                    try { File.Copy(logPath, logPath + ".old", true); } catch { }
                    try { File.Delete(logPath); } catch { }
                }
                File.AppendAllText(logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch { }
        }

        static string ExpandHome(string path)
        {
            if (path == "~")
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (path.StartsWith("~/") || path.StartsWith("~\\"))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    path.Substring(2));
            return path;
        }
    }

    // Menu palette matching the Windows light/dark mode.
    class ThemeColorTable : ProfessionalColorTable
    {
        readonly Color bg;
        readonly Color hover;
        readonly Color border;
        readonly Color separator;

        public ThemeColorTable(bool dark)
        {
            if (dark)
            {
                bg = Color.FromArgb(32, 32, 32);
                hover = Color.FromArgb(61, 61, 61);
                border = Color.FromArgb(74, 74, 74);
                separator = Color.FromArgb(58, 58, 58);
            }
            else
            {
                bg = Color.White;
                hover = Color.FromArgb(229, 241, 251);
                border = Color.FromArgb(201, 201, 201);
                separator = Color.FromArgb(224, 224, 224);
            }
        }

        public override Color ToolStripDropDownBackground { get { return bg; } }
        public override Color MenuItemSelected { get { return hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return hover; } }
        public override Color MenuItemBorder { get { return border; } }
        public override Color MenuBorder { get { return border; } }
        public override Color SeparatorDark { get { return separator; } }
        public override Color SeparatorLight { get { return separator; } }
        public override Color ImageMarginGradientBegin { get { return bg; } }
        public override Color ImageMarginGradientMiddle { get { return bg; } }
        public override Color ImageMarginGradientEnd { get { return bg; } }
        public override Color MenuItemPressedGradientBegin { get { return hover; } }
        public override Color MenuItemPressedGradientEnd { get { return hover; } }
        public override Color MenuItemPressedGradientMiddle { get { return hover; } }
        public override Color MenuStripGradientBegin { get { return bg; } }
        public override Color MenuStripGradientEnd { get { return bg; } }
        public override Color ToolStripBorder { get { return border; } }
    }
}
