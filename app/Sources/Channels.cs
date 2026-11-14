// Canali seguiti: ogni N minuti elenca gli ultimi VOD di ciascun canale (yt-dlp --flat-playlist) e mette in
// coda quelli nuovi. Il VOD della live in corso si salta finche' la live non e' finita (sarebbe a meta').
namespace MediaDownloader;

sealed class ChannelWatcher
{
    readonly JobQueue queue;
    readonly System.Windows.Forms.Timer timer = new();
    bool checking;
    public DateTime? LastCheck;
    public DateTime NextCheck = DateTime.Now;
    public readonly Dictionary<string, string> LastResult = [];   // canale -> esito dell'ultimo controllo
    public event Action<Channel, int>? Found;                        // nuovi VOD messi in coda
    public event Action? Changed;

    public ChannelWatcher(JobQueue queue)
    {
        this.queue = queue;
        timer.Tick += async (_, _) => await CheckAll();
        Reschedule(TimeSpan.FromSeconds(20)); // primo controllo poco dopo l'avvio
    }

    public void Reschedule(TimeSpan? delay = null)
    {
        var d = delay ?? TimeSpan.FromMinutes(Math.Max(5, Store.Settings.CheckMinutes));
        NextCheck = DateTime.Now + d;
        timer.Stop();
        timer.Interval = (int)Math.Min(int.MaxValue, d.TotalMilliseconds);
        timer.Start();
        Changed?.Invoke();
    }

    public bool Checking => checking;

    public async Task CheckAll()
    {
        timer.Stop();
        if (checking || Store.Demo) return;
        checking = true;
        Changed?.Invoke();
        try
        {
            if (!queue.Paused && Store.Settings.Channels.Any(c => c.Enabled))
            {
                await Tools.EnsureBasics(_ => { });
                await Tools.UpdateYtDlp();
                foreach (var c in Store.Settings.Channels.Where(c => c.Enabled).ToList())
                {
                    try { await Check(c); }
                    catch (Exception e)
                    {
                        LastResult[c.Key] = L("error: {0}", e.Message);
                        Log.Warn($"Check {c.Key} failed: {e.Message}");
                    }
                }
                LastCheck = DateTime.Now;
            }
        }
        finally
        {
            checking = false;
            Reschedule();
        }
    }

    async Task Check(Channel c)
    {
        var (code, lines) = await Runner.Capture(Tools.YtDlp,
            ["--flat-playlist", "--playlist-end", "15", "--encoding", "utf-8", "--no-warnings", "--print", "%(id)s\t%(url)s\t%(title)s", c.VideosUrl]);
        var entries = lines.Select(l => l.Split('\t')).Where(p => p.Length >= 3 && p[0] != "NA").Select(p => (id: p[0], url: p[1], title: p[2])).ToList();
        if (code != 0 && entries.Count == 0)
            throw new Exception(lines.LastOrDefault(l => l.StartsWith("ERROR:"))?[6..].Trim() ?? $"yt-dlp exit {code}");

        // canale appena aggiunto: i VOD che ci sono gia' non si scaricano, solo quelli da qui in avanti
        if (!Store.Seen.ContainsKey(c.Key))
        {
            Store.MarkSeen(c.Key, entries.Select(e => e.id));
            LastResult[c.Key] = L("following, {0} existing VODs skipped", entries.Count);
            Log.Info($"Now following {c.Key}: {entries.Count} existing VOD(s) marked as seen");
            return;
        }

        var seen = Store.Seen[c.Key];
        var fresh = entries.Where(e => !seen.Contains(e.id) && !queue.IsQueued(c.Key, e.id)).ToList();
        if (fresh.Count > 0 && entries.Count > 0 && fresh[0].id == entries[0].id && await IsLive(c))
        {
            fresh.RemoveAt(0); // il piu' recente e' la live ancora in corso
            Log.Info($"{c.Key} is live: newest VOD postponed until the stream ends");
        }
        fresh.Reverse(); // dal piu' vecchio al piu' recente
        foreach (var e in fresh)
            queue.Add(new Job
            {
                Kind = JobKind.Download, Url = e.url, Title = e.title, Audio = c.Audio, Transcribe = c.Transcribe, Chapters = c.Transcribe && c.Chapters,
                ChannelKey = c.Key, ChannelFolder = c.FolderName, VodId = e.id,
            });
        LastResult[c.Key] = fresh.Count > 0 ? L("{0} new", fresh.Count) : L("no new VODs");
        if (fresh.Count > 0) Found?.Invoke(c, fresh.Count);
    }

    // "Scarica l'ultimo VOD": il piu' recente gia' concluso (se il canale e' in live, quello prima)
    public async Task<string?> QueueLatest(Channel c)
    {
        await Tools.EnsureBasics(_ => { });
        var (code, lines) = await Runner.Capture(Tools.YtDlp,
            ["--flat-playlist", "--playlist-end", "2", "--encoding", "utf-8", "--no-warnings", "--print", "%(id)s\t%(url)s\t%(title)s", c.VideosUrl]);
        var entries = lines.Select(l => l.Split('\t')).Where(p => p.Length >= 3 && p[0] != "NA").ToList();
        if (entries.Count > 0 && await IsLive(c)) entries.RemoveAt(0);
        if (entries.Count == 0) return null;
        var e = entries[0];
        if (!queue.IsQueued(c.Key, e[0]))
            queue.Add(new Job
            {
                Kind = JobKind.Download, Url = e[1], Title = e[2], Audio = c.Audio, Transcribe = c.Transcribe, Chapters = c.Transcribe && c.Chapters,
                ChannelKey = c.Key, ChannelFolder = c.FolderName, VodId = e[0],
            });
        return e[2];
    }

    // yt-dlp riesce a estrarre la live solo se il canale e' in onda
    static async Task<bool> IsLive(Channel c)
    {
        var (code, lines) = await Runner.Capture(Tools.YtDlp, ["--skip-download", "--no-warnings", "--print", "is_live", c.LiveUrl]);
        return code == 0 && lines.Any(l => l.Trim() == "True");
    }

    // "https://www.twitch.tv/nome/videos" o "kick.com/nome" o "nome" -> nome
    public static string NameFromInput(string input)
    {
        var s = input.Trim().TrimEnd('/');
        if (Uri.TryCreate(s.Contains("://") ? s : "https://" + s, UriKind.Absolute, out var u) && u.Host.Contains('.'))
            return u.AbsolutePath.Trim('/').Split('/')[0];
        return s.TrimStart('@');
    }

    public static Platform? PlatformFromInput(string input) =>
        input.Contains("kick.com", StringComparison.OrdinalIgnoreCase) ? Platform.Kick
        : input.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase) ? Platform.Twitch : null;
}
