// L'app nella tray: link copiato o canale seguito -> download (yt-dlp) -> sottotitoli (Whisper).
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MediaDownloader;

sealed class TrayApp : ApplicationContext
{
    readonly SynchronizationContext ui;
    readonly NotifyIcon tray;
    readonly ContextMenuStrip menu = new() { ShowItemToolTips = true };
    readonly MessageWindow window = new();
    readonly System.Windows.Forms.Timer settle = new() { Interval = 300 };
    readonly Dictionary<State, Icon> icons = [];
    readonly HashSet<string> offered = [];          // link gia' proposti in questa sessione
    readonly JobQueue queue;
    readonly ChannelWatcher watcher;
    SettingsForm? settings;
    DownloadForm? downloadForm;
    string? lastError;
    Action? onBalloonClick;

    public static string Version => Application.ProductVersion.Split('+')[0];

    public TrayApp()
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        ui = SynchronizationContext.Current!;
        Store.Load();
        foreach (var s in Enum.GetValues<State>()) icons[s] = Icons.Tray(s);

        queue = new JobQueue(ui);
        queue.Changed += () => { UpdateState(); RefreshOpenMenu(); };
        queue.Finished += OnFinished;
        watcher = new ChannelWatcher(queue);
        watcher.Found += (c, n) => { if (Store.Settings.NotifySuccess) Notify(L("New VODs from {0}", c.Name), L("{0} queued for download.", n), false); };

        window.ClipboardChanged += () => { if (Store.Settings.OfferCopiedLinks && !Store.Demo) { settle.Stop(); settle.Start(); } };
        settle.Tick += (_, _) => { settle.Stop(); OnClipboardChanged(); };
        window.HotkeyPressed += () => ShowDownload(null);

        tray = new NotifyIcon { ContextMenuStrip = menu };
        menu.Opening += (_, e) => { BuildMenu(); e.Cancel = false; };
        tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) ShowMenuAtCursor(); };
        tray.BalloonTipClicked += (_, _) => { var a = onBalloonClick; onBalloonClick = null; a?.Invoke(); };
        tray.BalloonTipClosed += (_, _) => onBalloonClick = null;
        UpdateState();
        tray.Visible = true;

        Log.Info($"Started {Version}");
        if (!Store.Demo)
        {
            if (!ApplyHotkey())
                Notify(L("Shortcut not available"), L("{0} is already used by another app: choose another one in Settings.", Store.Settings.Hotkey), true, ShowSettings);
            if (Store.LoadError != null)
                Notify(L("Settings file can't be read"), Store.LoadError, true, OpenDataFolder);
            // yt-dlp e ffmpeg pronti prima del primo download; poi i lavori rimasti dall'ultima volta
            _ = PrepareTools();
        }
    }

    async Task PrepareTools()
    {
        try
        {
            if (!Tools.BasicsReady) Notify(L("Media Downloader is getting ready"), L("Downloading yt-dlp and ffmpeg (about 200 MB), only the first time."), false);
            await Tools.EnsureBasics(s => SetTooltip(s));
            await Tools.UpdateYtDlp();
        }
        catch (Exception e)
        {
            lastError = e.Message;
            Log.Error($"Tools setup failed: {e.Message}");
            Notify(L("Can't install yt-dlp/ffmpeg"), e.Message, true, OpenLog);
        }
        UpdateState();
        queue.Pump();
    }

    // ------------------------------------------------------------ comandi (menu, riga di comando)
    public void Run(string command) => ui.Post(_ => Execute(command), null);

    void Execute(string command)
    {
        Log.Info($"Command: {command}");
        var (name, arg) = command.Split('\t', 2) is [var n, var a] ? (n, a) : (command, "");
        switch (name)
        {
            case "download": AddDownload(arg, audio: false, Store.Settings.TranscribeVideo); break;
            case "audio": AddDownload(arg, audio: true, Store.Settings.TranscribeAudio); break;
            case "ask": ShowDownload(arg); break;
            case "transcribe": AddTranscription(arg); break;
            case "check": _ = watcher.CheckAll(); break;
            case "latest":
                if (Store.Settings.Channels.FirstOrDefault(c => c.Name.Equals(arg.Trim(), StringComparison.OrdinalIgnoreCase)) is Channel ch) _ = DownloadLatest(ch);
                else Notify(L("No VODs found"), arg, true);
                break;
            case "pause": SetPaused(true); break;
            case "resume": SetPaused(false); break;
            case "settings": ShowSettings(); break;
            case "menu": ShowMenuAtCursor(); break;
            case "quit": Quit(); break;
        }
    }

    public void AddDownload(string url, bool audio, bool transcribe)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
        {
            Notify(L("Not a link"), url, true);
            return;
        }
        queue.Add(new Job { Kind = JobKind.Download, Url = u.ToString(), Audio = audio, Transcribe = transcribe });
    }

    public void AddTranscription(string file)
    {
        if (!File.Exists(file)) { Notify(L("File not found"), file, true); return; }
        queue.Add(new Job { Kind = JobKind.Transcribe, File = file, Title = Path.GetFileNameWithoutExtension(file) });
    }

    // Link copiato: se e' di un sito noto o un file multimediale, propone il download
    void OnClipboardChanged()
    {
        var text = Clip.ReadText(window.Handle)?.Trim();
        if (text == null || text.Length > 2000 || text.Contains('\n') || !Links.Offerable(text) || !offered.Add(text)) return;
        Log.Info($"Link copied: {text}");
        var host = new Uri(text).Host.Replace("www.", "");
        Notify(L("Link copied from {0}", host), L("Click here to download it."), false, () => ShowDownload(text));
    }

    void OnFinished(Job job, string? error)
    {
        if (error != null)
        {
            lastError = error;
            UpdateState();
            Notify(job.Kind == JobKind.Download ? L("Download failed") : L("Transcription failed"), $"{job.Display}\n{error}", true, OpenLog);
            return;
        }
        lastError = null;
        UpdateState();
        if (!Store.Settings.NotifySuccess) return;
        var file = job.File;
        if (job.Kind == JobKind.Download)
            Notify(L("Downloaded"), job.Transcribe ? L("{0} — subtitles coming next.", job.Display) : job.Display, false, () => ShowFile(file));
        else
            Notify(L("Subtitles ready"), job.Display, false, () => ShowFile(file));
    }

    // ------------------------------------------------------------ stato
    public State State =>
        queue.Paused ? State.Paused
        : queue.Busy(JobKind.Download) ? State.Downloading
        : queue.Busy(JobKind.Transcribe) ? State.Transcribing
        : lastError != null ? State.Error : State.Ready;

    string StatusText
    {
        get
        {
            var active = queue.Active;
            int d = active.Count(j => j.Kind == JobKind.Download), t = active.Count(j => j.Kind == JobKind.Transcribe);
            return State switch
            {
                State.Paused => L("Paused — nothing new starts"),
                State.Error => L("Last job failed"),
                _ when d + t == 0 => L("Ready"),
                _ => string.Join(" · ", new[] { d > 0 ? L("{0} download(s)", d) : null, t > 0 ? L("{0} transcription(s)", t) : null }.Where(x => x != null)),
            };
        }
    }

    string ChannelsText
    {
        get
        {
            var n = Store.Settings.Channels.Count(c => c.Enabled);
            if (n == 0) return L("No channels followed");
            return watcher.Checking ? L("{0} channel(s) · checking now…", n) : L("{0} channel(s) · next check {1:HH:mm}", n, watcher.NextCheck);
        }
    }

    void UpdateState()
    {
        tray.Icon = icons[State];
        SetTooltip($"Media Downloader — {StatusText}");
    }

    void SetTooltip(string text) => ui.Post(_ => tray.Text = text.Length > 127 ? text[..127] : text, null);

    void SetPaused(bool value)
    {
        queue.Paused = value;
        Log.Info(value ? "Paused" : "Resumed");
        queue.Pump();
        UpdateState();
    }

    public bool ApplyHotkey()
    {
        if (Store.Demo) return true;
        var ok = window.SetHotkey(Store.Settings.Hotkey);
        if (!ok) Log.Warn($"Shortcut {Store.Settings.Hotkey} not available");
        return ok;
    }

    public void SettingsChanged()
    {
        ApplyHotkey();
        watcher.Reschedule(TimeSpan.FromSeconds(5)); // canali nuovi: un controllo subito
        queue.Pump();
        UpdateState();
    }

    // ------------------------------------------------------------ menu
    readonly Dictionary<Guid, ToolStripMenuItem> jobItems = [];

    void BuildMenu()
    {
        menu.Items.Clear();
        jobItems.Clear();
        var size = SystemInformation.SmallIconSize.Width;
        var dot = Icons.Dot(Icons.ColorOf(State), size);
        if (State == State.Error)
            menu.Items.Add(new ToolStripMenuItem(L("Last job failed — click to open the log"), dot, (_, _) => OpenLog()) { ToolTipText = lastError });
        else
            menu.Items.Add(new ToolStripLabel(StatusText, dot) { ImageAlign = ContentAlignment.MiddleLeft, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 3, 0, 1) });
        menu.Items.Add(new ToolStripLabel(queue.Waiting ?? ChannelsText, new Bitmap(size, size))
        {
            ForeColor = SystemColors.GrayText, ImageAlign = ContentAlignment.MiddleLeft, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 0, 3),
        });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem(L("Download link…"), null, (_, _) => ShowDownload(null)) { ShortcutKeyDisplayString = Store.Settings.Hotkey });
        menu.Items.Add(new ToolStripMenuItem(L("Transcribe a file…"), null, (_, _) => PickFileToTranscribe()));

        var active = queue.Active;
        if (active.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Header(L("Queue — click a job to cancel it")));
            foreach (var job in active.Take(12))
            {
                var j = job;
                var item = new ToolStripMenuItem(JobText(j), null, (_, _) => AskCancel(j)) { ShortcutKeyDisplayString = JobProgress(j), ToolTipText = j.Url != "" ? j.Url : j.File };
                jobItems[j.Id] = item;
                menu.Items.Add(item);
            }
        }

        if (Store.History.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Header(L("Recent — click to show the file")));
            foreach (var h in Store.History.Take(8))
            {
                var file = h.File;
                menu.Items.Add(new ToolStripMenuItem(Short(h.Title, 48), null, (_, _) => ShowFile(file))
                {
                    ShortcutKeyDisplayString = (h.Subtitles ? "CC · " : "") + Ago(h.Date),
                    ToolTipText = h.File,
                });
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        var channels = new ToolStripMenuItem(L("Channels"));
        channels.DropDownItems.Add(new ToolStripMenuItem(L("Check now"), null, (_, _) => _ = watcher.CheckAll()) { Enabled = !watcher.Checking && Store.Settings.Channels.Count > 0 });
        if (Store.Settings.Channels.Count > 0) channels.DropDownItems.Add(new ToolStripSeparator());
        foreach (var c in Store.Settings.Channels)
        {
            var folder = Path.Combine(Store.Settings.StreamFolder, c.FolderName);
            var channel = c;
            var item = new ToolStripMenuItem($"{c.Platform} · {c.Name}" + (c.Audio ? " (audio)" : ""))
            {
                ShortcutKeyDisplayString = !c.Enabled ? L("off") : watcher.LastResult.GetValueOrDefault(c.Key, ""),
            };
            item.DropDownItems.Add(new ToolStripMenuItem(L("Download the latest VOD"), null, (_, _) => _ = DownloadLatest(channel)));
            item.DropDownItems.Add(new ToolStripMenuItem(L("Open folder"), null, (_, _) => OpenFolder(folder)) { ToolTipText = folder });
            channels.DropDownItems.Add(item);
        }
        channels.DropDownItems.Add(new ToolStripSeparator());
        channels.DropDownItems.Add(new ToolStripMenuItem(L("Manage channels…"), null, (_, _) => ShowSettings(1)));
        menu.Items.Add(channels);
        menu.Items.Add(new ToolStripMenuItem(queue.Paused ? L("Resume") : L("Pause"), null, (_, _) => SetPaused(!queue.Paused)));
        var folders = new ToolStripMenuItem(L("Open folder"));
        folders.DropDownItems.Add(new ToolStripMenuItem(L("Twitch / Kick"), null, (_, _) => OpenFolder(Store.Settings.StreamFolder)) { ShortcutKeyDisplayString = Store.Settings.StreamFolder });
        folders.DropDownItems.Add(new ToolStripMenuItem(L("Everything else"), null, (_, _) => OpenFolder(Store.Settings.OtherFolder)) { ShortcutKeyDisplayString = Store.Settings.OtherFolder });
        menu.Items.Add(folders);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(L("Settings…"), null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripMenuItem(L("Open log"), null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(L("Quit"), null, (_, _) => Quit()));
    }

    public async Task DownloadLatest(Channel c)
    {
        try
        {
            var title = await watcher.QueueLatest(c);
            if (title == null) Notify(L("No VODs found"), $"{c.Platform} · {c.Name}", true);
            else Log.Info($"Latest VOD of {c.Key} queued: {title}");
        }
        catch (Exception e)
        {
            Log.Error($"Latest VOD of {c.Key}: {e.Message}");
            Notify(L("Download failed"), e.Message, true, OpenLog);
        }
    }

    static ToolStripMenuItem Header(string text) => new(text) { Enabled = false };

    static string JobText(Job j) => (j.Kind == JobKind.Download ? (j.Audio ? "♪ " : "↓ ") : "CC ") + Short(j.Display, 46);

    static string JobProgress(Job j) => j.State == JobState.Queued ? L("queued") : j.Progress;

    // Avanzamento aggiornato mentre il menu e' aperto
    void RefreshOpenMenu()
    {
        if (!menu.Visible) return;
        foreach (var j in queue.Active)
            if (jobItems.TryGetValue(j.Id, out var item))
            {
                item.Text = JobText(j);
                item.ShortcutKeyDisplayString = JobProgress(j);
            }
    }

    void AskCancel(Job job)
    {
        if (MessageBox.Show(L("Cancel “{0}”?", job.Display), "Media Downloader", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            queue.CancelJob(job);
    }

    static string Short(string s, int n) => s.Length > n ? s[..(n - 1)] + "…" : s;

    static string Ago(DateTime d)
    {
        var t = DateTime.Now - d;
        return t.TotalMinutes < 1 ? L("just now")
             : t.TotalHours < 1 ? L("{0} min ago", (int)t.TotalMinutes)
             : t.TotalDays < 1 ? L("{0} h ago", (int)t.TotalHours)
             : L("{0} d ago", (int)t.TotalDays);
    }

    void ShowMenuAtCursor() =>
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(tray, null);

    // ------------------------------------------------------------ finestre e azioni
    public void ShowDownload(string? url)
    {
        url ??= Clip.ReadText(window.Handle)?.Trim() is string t && Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http") ? t : "";
        if (downloadForm != null && !downloadForm.IsDisposed) downloadForm.Close();
        downloadForm = new DownloadForm(url, this);
        downloadForm.Show();
        downloadForm.Activate();
    }

    void PickFileToTranscribe()
    {
        using var d = new OpenFileDialog
        {
            Title = L("Transcribe a file"),
            Filter = L("Video and audio") + "|*.mkv;*.mp4;*.webm;*.mov;*.avi;*.m4v;*.mp3;*.m4a;*.opus;*.ogg;*.wav;*.flac;*.aac|" + L("All files") + "|*.*",
            Multiselect = true,
        };
        if (d.ShowDialog() == DialogResult.OK) foreach (var f in d.FileNames) AddTranscription(f);
    }

    public void ShowSettings() => ShowSettings(0);

    void ShowSettings(int tab)
    {
        if (settings == null || settings.IsDisposed) settings = new SettingsForm(this, watcher);
        settings.SelectTab(tab);
        settings.Show();
        if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
        settings.Activate();
    }

    static void ShowFile(string file)
    {
        if (File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
        else OpenFolder(Path.GetDirectoryName(file)!);
    }

    static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception e) { Log.Warn($"Can't open {folder}: {e.Message}"); }
    }

    static void OpenLog()
    {
        if (!File.Exists(Log.File)) Log.Info("Log opened");
        Process.Start(new ProcessStartInfo(Log.File) { UseShellExecute = true });
    }

    static void OpenDataFolder() => OpenFolder(Store.Dir);

    public void Notify(string title, string text, bool error, Action? onClick = null)
    {
        if (Store.Demo) return;
        ui.Post(_ =>
        {
            onBalloonClick = onClick;
            tray.ShowBalloonTip(6000, title, text.Length > 250 ? text[..250] + "…" : text, error ? ToolTipIcon.Error : ToolTipIcon.None);
        }, null);
    }

    void Quit()
    {
        Log.Info("Quit");
        queue.Shutdown(); // i lavori interrotti ripartono al prossimo avvio
        tray.Visible = false;
        window.Dispose();
        settings?.Close();
        ExitThread();
    }

    // ------------------------------------------------------------ immagini del README (--render-docs, dati finti)
    public void RenderDocs(string dir)
    {
        Directory.CreateDirectory(dir);
        queue.AddDemo(new Job { Kind = JobKind.Download, Url = "https://www.twitch.tv/videos/1", Title = "Saturday marathon — part 2", Progress = L("{0} · {1} · {2} left", "42.0%", "38.5MiB/s", "04:12"), State = JobState.Running });
        queue.AddDemo(new Job { Kind = JobKind.Transcribe, File = @"D:\Media\Streaming\somestreamer\x.mkv", Title = "Late night chill stream", Progress = L("Transcribing… {0}%", 63), State = JobState.Running });
        queue.AddDemo(new Job { Kind = JobKind.Download, Url = "https://soundcloud.com/x/y", Audio = true, Title = "Deep house mix vol. 7", State = JobState.Queued });
        var steps = new Queue<Action>();
        steps.Enqueue(() =>
        {
            var a = Screen.PrimaryScreen!.WorkingArea;
            BuildMenu();
            // lontano dal mouse, se no una voce risulta evidenziata
            var x = Cursor.Position.X > a.Left + a.Width / 2 ? a.Left + 40 : a.Right - 700;
            menu.Show(new Point(x, a.Top + 40));
        });
        steps.Enqueue(() =>
        {
            Capture(menu.Bounds, Path.Combine(dir, "menu.png"));
            menu.Close();
            ShowSettings(1);
            settings!.TopMost = true;
        });
        steps.Enqueue(() =>
        {
            Capture(WindowRect(settings!.Handle), Path.Combine(dir, "settings.png"));
            settings.Close();
            ShowDownload("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
            downloadForm!.TopMost = true;
            downloadForm.BringToFront();
        });
        steps.Enqueue(() => { }); // un secondo in piu' perche' il dialogo sia disegnato
        steps.Enqueue(() =>
        {
            Capture(WindowRect(downloadForm!.Handle), Path.Combine(dir, "download.png"));
            downloadForm.Close();
            Icons.SaveStates(Path.Combine(dir, "states.png"), Enum.GetValues<State>().Select(s => (s, s switch
            {
                State.Ready => L("Ready"),
                State.Downloading => L("Downloading"),
                State.Transcribing => L("Transcribing"),
                State.Paused => L("Paused"),
                _ => L("Error"),
            })));
            Quit();
        });
        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) => { if (steps.Count > 0) steps.Dequeue()(); else timer.Stop(); };
        timer.Start();
    }

    static void Capture(Rectangle r, string file)
    {
        using var b = new Bitmap(r.Width, r.Height);
        using (var g = Graphics.FromImage(b)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        b.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }

    static Rectangle WindowRect(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var r, Marshal.SizeOf<RECT>()) == 0
            ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
            : Control.FromHandle(hwnd)!.Bounds;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
}

// Quali link copiati proporre: i siti principali e i link diretti a file audio/video
static class Links
{
    static readonly string[] hosts = ["youtube.com", "youtu.be", "twitch.tv", "kick.com", "soundcloud.com", "vimeo.com", "x.com", "twitter.com", "tiktok.com", "instagram.com", "reddit.com", "bandcamp.com", "dailymotion.com"];
    static readonly string[] extensions = [".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v", ".mp3", ".m4a", ".flac", ".wav", ".ogg", ".opus", ".aac", ".m3u8"];

    public static bool Offerable(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return false;
        var host = u.Host.ToLowerInvariant();
        if (hosts.Any(h => host == h || host.EndsWith("." + h)))
            return u.AbsolutePath.Trim('/') != ""; // non la home page del sito
        return extensions.Any(e => u.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }
}
