// Finestre: download di un link, impostazioni (generali, canali, trascrizione), modifica di un canale.
using System.Diagnostics;

namespace MediaDownloader;

static class Ui
{
    public static readonly Color Ok = Color.FromArgb(0x2E, 0xA0, 0x43), Bad = Color.FromArgb(0xD9, 0x34, 0x2B);

    public static void Setup(Form f, string title)
    {
        f.Text = title;
        f.Icon = Icons.App();
        f.AutoScaleDimensions = new SizeF(96F, 96F);
        f.AutoScaleMode = AutoScaleMode.Dpi;
        f.Padding = new Padding(14);
    }

    public static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(0, 10, 0, 4),
        Font = new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, FontStyle.Bold),
    };

    public static Label Note(string text, int width = 560) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 6),
    };

    public static Label Label(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 0) };

    // Griglia a due colonne (etichetta + controllo) che si ridimensiona da sola
    public static TableLayoutPanel Grid(int columns = 2)
    {
        var g = new TableLayoutPanel { AutoSize = true, ColumnCount = columns, Dock = DockStyle.Top, Margin = Padding.Empty };
        g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 2; i < columns; i++) g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return g;
    }

    public static void Row(TableLayoutPanel g, string label, Control input, Control? extra = null)
    {
        var r = g.RowCount++;
        g.Controls.Add(Label(label), 0, r);
        input.AccessibleName = label;
        if (input is TextBox or ComboBox) input.Dock = DockStyle.Fill;
        g.Controls.Add(input, 1, r);
        if (extra != null) g.Controls.Add(extra, 2, r);
    }

    public static void Full(TableLayoutPanel g, Control c)
    {
        var r = g.RowCount++;
        g.Controls.Add(c, 0, r);
        g.SetColumnSpan(c, g.ColumnCount);
    }

    public static Button FolderButton(TextBox target) => new()
    {
        Text = L("Browse…"), AutoSize = true,
        Tag = target,
    };

    public static void BrowseFolder(TextBox target)
    {
        using var d = new FolderBrowserDialog { SelectedPath = target.Text, UseDescriptionForTitle = true, Description = L("Choose a folder") };
        if (d.ShowDialog() == DialogResult.OK) target.Text = d.SelectedPath;
    }

    public static FlowLayoutPanel Buttons(params Button[] buttons)
    {
        var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Dock = DockStyle.Bottom, Margin = new Padding(0, 10, 0, 0) };
        p.Controls.AddRange(buttons);
        return p;
    }
}

// ================================================================ download di un link
sealed class DownloadForm : Form
{
    readonly TextBox url = new();
    readonly RadioButton video = new() { Text = L("Video"), AutoSize = true, Checked = true };
    readonly RadioButton audio = new() { Text = L("Audio only"), AutoSize = true };
    readonly CheckBox transcribe = new() { Text = L("Subtitles with Whisper"), AutoSize = true };
    readonly Label destination = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(520, 0), Margin = new Padding(0, 6, 0, 0) };

    public DownloadForm(string link, TrayApp app)
    {
        SuspendLayout();
        Ui.Setup(this, L("Download — Media Downloader"));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ShowInTaskbar = true;

        var g = Ui.Grid();
        g.ColumnStyles[1] = new ColumnStyle(SizeType.Absolute, 440);
        Ui.Row(g, L("Link"), url);
        var kind = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
        kind.Controls.AddRange([video, audio]);
        Ui.Row(g, L("Download"), kind);
        Ui.Row(g, "", transcribe);
        Ui.Full(g, destination);
        var ok = new Button { Text = L("Download"), AutoSize = true };
        var cancel = new Button { Text = L("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        root.Controls.Add(g);
        root.Controls.Add(Ui.Buttons(cancel, ok));
        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
        ResumeLayout();

        url.Text = link;
        void Update()
        {
            transcribe.Checked = audio.Checked ? Store.Settings.TranscribeAudio : Store.Settings.TranscribeVideo;
            ShowDestination();
        }
        video.CheckedChanged += (_, _) => Update();
        url.TextChanged += (_, _) => ShowDestination();
        Update();
        cancel.Click += (_, _) => Close();
        ok.Click += (_, _) =>
        {
            if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            {
                destination.ForeColor = Ui.Bad;
                destination.Text = L("Paste a link (https://…).");
                url.Focus();
                return;
            }
            app.AddDownload(u.ToString(), audio.Checked, transcribe.Checked);
            Close();
        };
        Shown += (_, _) => { if (url.Text == "") url.Focus(); else ok.Focus(); };
    }

    void ShowDestination()
    {
        destination.ForeColor = SystemColors.GrayText;
        var stream = JobQueue.IsStream(url.Text.Trim());
        destination.Text = L("Saved to {0}", stream ? Path.Combine(Store.Settings.StreamFolder, L("<streamer>")) : Store.Settings.OtherFolder);
    }
}

// ================================================================ impostazioni
sealed class SettingsForm : Form
{
    readonly TrayApp app;
    readonly ChannelWatcher watcher;
    readonly TabControl tabs = new() { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
    readonly List<Channel> channels;

    // generali
    readonly TextBox streamFolder = new(), otherFolder = new(), workFolder = new(), streamTemplate = new(), otherTemplate = new(), cookies = new();
    readonly ComboBox quality = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox audioFormat = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly NumericUpDown parallel = new() { Minimum = 1, Maximum = 6, Width = 60 };
    readonly CheckBox offerLinks = new() { Text = L("Offer to download links I copy (YouTube, Twitch, Kick, SoundCloud, direct media links…)"), AutoSize = true };
    readonly CheckBox hotkeyOn = new() { Text = L("Shortcut to download the copied link:"), AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    readonly HotkeyBox hotkey = new() { Width = 130, AccessibleName = L("Shortcut") };
    readonly CheckBox notify = new() { Text = L("Notify when a download or transcription finishes (errors are always shown)"), AutoSize = true };
    readonly CheckBox startup = new() { Text = L("Start with Windows"), AutoSize = true };
    // canali
    readonly ListView list = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    readonly NumericUpDown interval = new() { Minimum = 5, Maximum = 1440, Width = 70 };
    readonly NumericUpDown minFree = new() { Minimum = 0, Maximum = 10000, Width = 70 };
    // trascrizione
    readonly CheckBox subsVideo = new() { Text = L("Video downloads"), AutoSize = true };
    readonly CheckBox subsAudio = new() { Text = L("Audio downloads"), AutoSize = true };
    readonly CheckBox embed = new() { Text = L("Also add the subtitles as a track inside the video (mkv/mp4, no re-encoding)"), AutoSize = true };
    readonly ComboBox model = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox language = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Label toolsStatus = new() { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 4, 0, 4) };

    static readonly (int h, string label)[] qualities = [(0, "Best available"), (2160, "Up to 2160p"), (1440, "Up to 1440p"), (1080, "Up to 1080p"), (720, "Up to 720p"), (480, "Up to 480p")];
    static readonly string[] audioFormats = ["mp3", "m4a", "opus", "best"];
    static readonly string[] languages = ["auto", "it", "en", "es", "fr", "de", "pt", "ru", "ja", "ko", "zh"];

    public SettingsForm(TrayApp app, ChannelWatcher watcher)
    {
        this.app = app;
        this.watcher = watcher;
        channels = Store.Settings.Channels.Select(c => c.Clone()).ToList();
        SuspendLayout();
        Ui.Setup(this, L("Media Downloader — Settings"));
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(700, 600);
        MinimumSize = new Size(640, 560);

        tabs.TabPages.Add(GeneralTab());
        tabs.TabPages.Add(ChannelsTab());
        tabs.TabPages.Add(TranscriptionTab());
        var save = new Button { Text = L("Save"), AutoSize = true };
        var cancel = new Button { Text = L("Cancel"), AutoSize = true };
        var about = new LinkLabel { Text = $"Media Downloader {TrayApp.Version} · made by mdrzn · GitHub", AutoSize = true, Anchor = AnchorStyles.Left, LinkColor = SystemColors.HotTrack, Margin = new Padding(0, 16, 0, 0) };
        about.LinkArea = new LinkArea(about.Text.Length - 6, 6);
        about.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("https://github.com/mdrzn/media-downloader") { UseShellExecute = true });
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(about, 0, 0);
        var buttons = Ui.Buttons(cancel, save);
        buttons.Dock = DockStyle.None;
        buttons.Anchor = AnchorStyles.Right;
        bottom.Controls.Add(buttons, 1, 0);
        Controls.Add(tabs);
        Controls.Add(bottom);
        AcceptButton = save;
        CancelButton = cancel;
        ResumeLayout();

        save.Click += (_, _) => { if (Apply()) Close(); };
        cancel.Click += (_, _) => Close();
        Load();
        Shown += async (_, _) => await ShowToolsStatus();
    }

    public void SelectTab(int i) => tabs.SelectedIndex = Math.Clamp(i, 0, tabs.TabCount - 1);

    TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(12), AutoScroll = true };
        page.Controls.Add(content);
        return page;
    }

    TabPage GeneralTab()
    {
        var g = Ui.Grid(3);
        Ui.Full(g, Ui.Heading(L("Folders")));
        Ui.Row(g, L("Twitch / Kick"), streamFolder, Browse(streamFolder));
        Ui.Row(g, L("Everything else"), otherFolder, Browse(otherFolder));
        Ui.Full(g, Ui.Note(L("Twitch and Kick go into one subfolder per streamer (ready for a Jellyfin library); YouTube, SoundCloud and everything else go into the second folder.")));
        Ui.Row(g, L("Working folder"), workFolder, Browse(workFolder));
        Ui.Full(g, Ui.Note(L("Files are downloaded and processed here (put it on a fast SSD), then moved to their folder in one go. Empty = the Windows temporary folder.")));
        Ui.Row(g, L("Twitch / Kick file names"), streamTemplate);
        Ui.Row(g, L("Other file names"), otherTemplate);
        Ui.Full(g, Ui.Note(L("yt-dlp output templates, e.g. %(title)s, %(upload_date>%Y-%m-%d)s, %(id)s.")));

        Ui.Full(g, Ui.Heading(L("Downloads")));
        foreach (var (_, label) in qualities) quality.Items.Add(L(label));
        Ui.Row(g, L("Video quality"), quality);
        foreach (var f in audioFormats) audioFormat.Items.Add(f == "best" ? L("original (no conversion)") : f);
        Ui.Row(g, L("Audio format"), audioFormat);
        Ui.Row(g, L("Downloads at the same time"), parallel);
        Ui.Row(g, L("Cookies file (optional)"), cookies, FileButton(cookies));
        Ui.Full(g, Ui.Note(L("A cookies.txt exported from your browser lets yt-dlp download subscriber-only VODs and age-restricted videos.")));

        Ui.Full(g, Ui.Heading(L("General")));
        Ui.Full(g, offerLinks);
        var hk = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
        hk.Controls.AddRange([hotkeyOn, hotkey]);
        Ui.Full(g, hk);
        Ui.Full(g, notify);
        Ui.Full(g, startup);
        hotkeyOn.CheckedChanged += (_, _) => hotkey.Enabled = hotkeyOn.Checked;
        return Page(L("General"), g);
    }

    TabPage ChannelsTab()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(Ui.Note(L("New VODs of these channels are downloaded automatically once the stream has ended, then transcribed if enabled. When you add a channel, its existing VODs are skipped.")));
        list.Columns.Add(L("Platform"), 80);
        list.Columns.Add(L("Channel"), 160);
        list.Columns.Add(L("Download"), 110);
        list.Columns.Add(L("Last check"), 220);
        root.Controls.Add(list);

        var add = new Button { Text = L("Add…"), AutoSize = true };
        var edit = new Button { Text = L("Edit…"), AutoSize = true };
        var remove = new Button { Text = L("Remove"), AutoSize = true };
        var check = new Button { Text = L("Check now"), AutoSize = true };
        var latest = new Button { Text = L("Download the latest VOD"), AutoSize = true, Enabled = false };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        buttons.Controls.AddRange([add, edit, remove, check, latest]);
        root.Controls.Add(buttons);

        var g = Ui.Grid(3);
        g.ColumnStyles[1] = new ColumnStyle(SizeType.AutoSize);
        g.ColumnStyles[2] = new ColumnStyle(SizeType.Percent, 100);
        Ui.Row(g, L("Check every"), interval, Ui.Label(L("minutes")));
        Ui.Row(g, L("Pause automatic downloads below"), minFree, Ui.Label(L("GB free on the disk")));
        root.Controls.Add(g);

        add.Click += (_, _) =>
        {
            using var f = new ChannelForm(null);
            if (f.ShowDialog(this) != DialogResult.OK) return;
            if (channels.Any(c => c.Key == f.Result!.Key)) return;
            channels.Add(f.Result!);
            RefreshList();
        };
        edit.Click += (_, _) => Edit();
        list.DoubleClick += (_, _) => Edit();
        remove.Click += (_, _) =>
        {
            if (Selected is not Channel c) return;
            if (MessageBox.Show(this, L("Stop following {0}? Files already downloaded are kept.", c.Name), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            channels.Remove(c);
            RefreshList();
        };
        check.Click += async (_, _) =>
        {
            if (!Apply()) return;
            check.Enabled = false;
            await watcher.CheckAll();
            if (IsDisposed) return;
            check.Enabled = true;
            RefreshList();
        };
        latest.Click += async (_, _) =>
        {
            if (Selected is not Channel c || !Apply()) return;
            latest.Enabled = false;
            await app.DownloadLatest(Store.Settings.Channels.First(x => x.Key == c.Key));
            if (!IsDisposed) latest.Enabled = true;
        };
        list.SelectedIndexChanged += (_, _) => edit.Enabled = remove.Enabled = latest.Enabled = Selected != null;
        list.Resize += (_, _) => list.Columns[3].Width = Math.Max(120, list.ClientSize.Width - list.Columns[0].Width - list.Columns[1].Width - list.Columns[2].Width);
        return Page(L("Channels"), root);
    }

    TabPage TranscriptionTab()
    {
        var g = Ui.Grid(2);
        Ui.Full(g, Ui.Note(L("Subtitles are made on this PC with Whisper (whisper.cpp, on the GPU when there is an NVIDIA card): an .srt file next to the video, named for Jellyfin (video.en.srt). Whisper is downloaded the first time it's needed (about 1.1 GB, plus the model).")));
        Ui.Full(g, Ui.Heading(L("Add subtitles by default to")));
        Ui.Full(g, subsVideo);
        Ui.Full(g, subsAudio);
        Ui.Full(g, Ui.Note(L("Followed channels have their own setting in the Channels tab.")));
        Ui.Full(g, embed);
        Ui.Full(g, Ui.Heading(L("Whisper")));
        model.Items.AddRange([L("large-v3-turbo — fast, about 1.6 GB"), L("large-v3 — most accurate, slower, about 3 GB")]);
        Ui.Row(g, L("Model"), model);
        foreach (var l in languages) language.Items.Add(l == "auto" ? L("detect automatically") : l);
        Ui.Row(g, L("Language"), language);
        Ui.Full(g, Ui.Heading(L("Tools")));
        Ui.Full(g, toolsStatus);
        var reinstall = new Button { Text = L("Reinstall tools"), AutoSize = true };
        var update = new Button { Text = L("Update yt-dlp now"), AutoSize = true };
        var p = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        p.Controls.AddRange([update, reinstall]);
        Ui.Full(g, p);
        update.Click += async (_, _) => { update.Enabled = false; await Tools.UpdateYtDlp(force: true); if (!IsDisposed) { update.Enabled = true; await ShowToolsStatus(); } };
        reinstall.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, L("Delete yt-dlp, ffmpeg and Whisper and download them again? (Models are kept.)"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            reinstall.Enabled = false;
            try
            {
                Tools.DeleteBinaries();
                await Tools.EnsureBasics(s => toolsStatus.Text = s);
            }
            catch (Exception e) { toolsStatus.Text = e.Message; }
            if (!IsDisposed) { reinstall.Enabled = true; await ShowToolsStatus(); }
        };
        return Page(L("Transcription"), g);
    }

    async Task ShowToolsStatus()
    {
        var v = await Tools.YtDlpVersion();
        if (IsDisposed) return;
        string Mark(bool ok) => ok ? "✓" : "—";
        toolsStatus.Text = string.Join("\n",
            $"{Mark(v != "")} yt-dlp {v}",
            $"{Mark(File.Exists(Tools.FFmpeg))} ffmpeg",
            $"{Mark(File.Exists(Tools.WhisperCli))} Whisper (whisper.cpp{(Tools.HasNvidiaGpu ? ", CUDA" : ", CPU")})",
            string.Join("   ", Tools.Models.Select(m => $"{Mark(File.Exists(Tools.Model(m)))} {m}")),
            L("Installed in {0}", Tools.Dir));
    }

    Button Browse(TextBox target)
    {
        var b = Ui.FolderButton(target);
        b.Click += (_, _) => Ui.BrowseFolder(target);
        return b;
    }

    Button FileButton(TextBox target)
    {
        var b = new Button { Text = L("Browse…"), AutoSize = true };
        b.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "cookies.txt|*.txt|" + L("All files") + "|*.*" };
            if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.FileName;
        };
        return b;
    }

    Channel? Selected => list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as Channel : null;

    void Edit()
    {
        if (Selected is not Channel c) return;
        using var f = new ChannelForm(c);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        channels[channels.IndexOf(c)] = f.Result!;
        RefreshList();
    }

    void RefreshList()
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var c in channels)
        {
            var item = new ListViewItem(c.Platform.ToString()) { Tag = c, ForeColor = c.Enabled ? list.ForeColor : SystemColors.GrayText };
            item.SubItems.Add(c.Name);
            item.SubItems.Add((c.Audio ? L("audio") : L("video")) + (c.Transcribe ? " + CC" : "") + (c.Transcribe && c.Chapters && !c.Audio ? " + §" : ""));
            item.SubItems.Add(!c.Enabled ? L("off") : watcher.LastResult.GetValueOrDefault(c.Key, L("not checked yet")));
            list.Items.Add(item);
        }
        list.EndUpdate();
    }

    new void Load()
    {
        var s = Store.Settings;
        streamFolder.Text = s.StreamFolder;
        otherFolder.Text = s.OtherFolder;
        streamTemplate.Text = s.StreamTemplate;
        otherTemplate.Text = s.OtherTemplate;
        cookies.Text = s.CookiesFile;
        workFolder.Text = s.WorkFolder;
        quality.SelectedIndex = Math.Max(0, Array.FindIndex(qualities, q => q.h == s.MaxHeight));
        audioFormat.SelectedIndex = Math.Max(0, Array.IndexOf(audioFormats, s.AudioFormat));
        parallel.Value = Math.Clamp(s.ParallelDownloads, 1, 6);
        offerLinks.Checked = s.OfferCopiedLinks;
        hotkeyOn.Checked = s.Hotkey != "";
        hotkey.Text = s.Hotkey == "" ? "Ctrl+Alt+D" : s.Hotkey;
        hotkey.Enabled = hotkeyOn.Checked;
        notify.Checked = s.NotifySuccess;
        startup.Checked = AutoStart.Enabled;
        interval.Value = Math.Clamp(s.CheckMinutes, 5, 1440);
        minFree.Value = Math.Clamp(s.MinFreeGb, 0, 10000);
        subsVideo.Checked = s.TranscribeVideo;
        subsAudio.Checked = s.TranscribeAudio;
        embed.Checked = s.EmbedSubtitles;
        model.SelectedIndex = Math.Max(0, Array.IndexOf(Tools.Models, s.WhisperModel));
        language.SelectedIndex = Math.Max(0, Array.IndexOf(languages, s.Language));
        RefreshList();
    }

    bool Apply()
    {
        foreach (var (box, name) in new[] { (streamFolder, L("Twitch / Kick")), (otherFolder, L("Everything else")) })
            if (box.Text.Trim() == "" || !Path.IsPathFullyQualified(box.Text.Trim()))
            {
                tabs.SelectedIndex = 0;
                MessageBox.Show(this, L("Choose a full folder path for “{0}”.", name), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                box.Focus();
                return false;
            }
        var s = Store.Settings;
        s.StreamFolder = streamFolder.Text.Trim();
        s.OtherFolder = otherFolder.Text.Trim();
        s.StreamTemplate = streamTemplate.Text.Trim() == "" ? new Settings().StreamTemplate : streamTemplate.Text.Trim();
        s.OtherTemplate = otherTemplate.Text.Trim() == "" ? new Settings().OtherTemplate : otherTemplate.Text.Trim();
        s.CookiesFile = cookies.Text.Trim();
        s.WorkFolder = workFolder.Text.Trim();
        s.MaxHeight = qualities[Math.Max(0, quality.SelectedIndex)].h;
        s.AudioFormat = audioFormats[Math.Max(0, audioFormat.SelectedIndex)];
        s.ParallelDownloads = (int)parallel.Value;
        s.OfferCopiedLinks = offerLinks.Checked;
        s.Hotkey = hotkeyOn.Checked ? hotkey.Text : "";
        s.NotifySuccess = notify.Checked;
        s.CheckMinutes = (int)interval.Value;
        s.MinFreeGb = (int)minFree.Value;
        s.TranscribeVideo = subsVideo.Checked;
        s.TranscribeAudio = subsAudio.Checked;
        s.EmbedSubtitles = embed.Checked;
        s.WhisperModel = Tools.Models[Math.Max(0, model.SelectedIndex)];
        s.Language = languages[Math.Max(0, language.SelectedIndex)];
        s.Channels = channels.Select(c => c.Clone()).ToList();
        if (startup.Checked != AutoStart.Enabled) AutoStart.Enabled = startup.Checked;
        Store.Save();
        Log.Info("Settings saved");
        app.SettingsChanged();
        return true;
    }
}

// ================================================================ aggiungi / modifica un canale
sealed class ChannelForm : Form
{
    readonly ComboBox platform = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TextBox name = new(), folder = new();
    readonly RadioButton video = new() { Text = L("Video"), AutoSize = true, Checked = true };
    readonly RadioButton audio = new() { Text = L("Audio only"), AutoSize = true };
    readonly CheckBox transcribe = new() { Text = L("Subtitles with Whisper"), AutoSize = true, Checked = true };
    readonly CheckBox chapters = new() { Text = L("Topic chapters with Claude (replace Twitch's category chapters)"), AutoSize = true, Checked = true };
    readonly CheckBox enabled = new() { Text = L("Check this channel"), AutoSize = true, Checked = true };
    readonly Label error = new() { AutoSize = true, ForeColor = Ui.Bad };
    public Channel? Result;

    public ChannelForm(Channel? c)
    {
        SuspendLayout();
        Ui.Setup(this, c == null ? L("Follow a channel") : L("Edit channel"));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        platform.Items.AddRange(["Twitch", "Kick"]);
        var g = Ui.Grid();
        g.ColumnStyles[1] = new ColumnStyle(SizeType.Absolute, 320);
        Ui.Row(g, L("Platform"), platform);
        Ui.Row(g, L("Channel"), name);
        Ui.Full(g, Ui.Note(L("The name, or the channel link: twitch.tv/name, kick.com/name."), 440));
        Ui.Row(g, L("Folder"), folder);
        Ui.Full(g, Ui.Note(L("Subfolder of the Twitch / Kick folder; empty = the channel name."), 440));
        var kind = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
        kind.Controls.AddRange([video, audio]);
        Ui.Row(g, L("Download"), kind);
        Ui.Row(g, "", transcribe);
        Ui.Row(g, "", chapters);
        Ui.Row(g, "", enabled);
        Ui.Full(g, error);
        var ok = new Button { Text = L("Save"), AutoSize = true };
        var cancel = new Button { Text = L("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        root.Controls.Add(g);
        root.Controls.Add(Ui.Buttons(cancel, ok));
        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
        ResumeLayout();

        platform.SelectedIndex = c?.Platform == Platform.Kick ? 1 : 0;
        name.Text = c?.Name ?? "";
        folder.Text = c?.Folder ?? "";
        audio.Checked = c?.Audio ?? false;
        video.Checked = !audio.Checked;
        transcribe.Checked = c?.Transcribe ?? true;
        chapters.Checked = c?.Chapters ?? true;
        chapters.Enabled = transcribe.Checked; // i capitoli nascono dalla trascrizione
        transcribe.CheckedChanged += (_, _) => chapters.Enabled = transcribe.Checked;
        enabled.Checked = c?.Enabled ?? true;
        // incollando un link, la piattaforma si sceglie da sola
        name.TextChanged += (_, _) => { if (ChannelWatcher.PlatformFromInput(name.Text) is Platform p) platform.SelectedIndex = (int)p; };
        ok.Click += (_, _) =>
        {
            var n = ChannelWatcher.NameFromInput(name.Text);
            if (n == "" || n.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-')))
            {
                error.Text = L("Enter the channel name or link.");
                name.Focus();
                return;
            }
            Result = new Channel
            {
                Platform = (Platform)platform.SelectedIndex, Name = n.ToLowerInvariant(), Folder = folder.Text.Trim(),
                Audio = audio.Checked, Transcribe = transcribe.Checked, Chapters = chapters.Checked, Enabled = enabled.Checked,
            };
            DialogResult = DialogResult.OK;
        };
    }
}

// Casella che registra la combinazione premuta: "Ctrl+Alt+D"
sealed class HotkeyBox : TextBox
{
    public HotkeyBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        BackColor = SystemColors.Window;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;
        if (modifiers == Keys.None && key == Keys.Tab) return base.ProcessCmdKey(ref msg, keyData);
        if (Hotkeys.Valid(modifiers, key)) Text = Hotkeys.Format(modifiers, key);
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;
    }
}
