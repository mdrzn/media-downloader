// Coda dei lavori: download (yt-dlp) e trascrizioni (whisper-cli). Download in parallelo fino al limite
// impostato, trascrizioni una alla volta (la GPU e' una). La coda sopravvive a un riavvio.
// Tutto avviene nella cartella di lavoro (un disco veloce): download, correzioni di ffmpeg, Whisper,
// traccia sottotitoli. Nella cartella finale (magari un disco USB lento) il file arriva con una sola copia.
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MediaDownloader;

enum JobKind { Download, Transcribe }
enum JobState { Queued, Running, Done, Failed, Canceled }

sealed class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public JobKind Kind { get; set; }
    public string Url { get; set; } = "";            // download
    public bool Audio { get; set; }
    public bool Transcribe { get; set; }
    public string? ChannelKey { get; set; }          // VOD di un canale seguito
    public string? ChannelFolder { get; set; }
    public string? VodId { get; set; }
    public string File { get; set; } = "";           // trascrizione: file da trascrivere; download: file scaricato
    public string? WorkDir { get; set; }             // cartella di lavoro del download (si riprende da qui dopo un riavvio)
    public string? Destination { get; set; }         // dove va il file finito (null = resta dov'e')
    public string Title { get; set; } = "";
    public DateTime Added { get; set; } = DateTime.Now;
    [JsonIgnore] public JobState State { get; set; }
    [JsonIgnore] public string Progress { get; set; } = "";
    [JsonIgnore] public double Percent { get; set; } = -1;
    [JsonIgnore] public CancellationTokenSource? Cancel { get; set; }
    [JsonIgnore] public string Display => Title != "" ? Title : Kind == JobKind.Transcribe ? Path.GetFileName(File) : Url;
}

sealed class JobQueue
{
    readonly SynchronizationContext ui;
    readonly List<Job> jobs = [];
    public event Action? Changed;
    public event Action<Job, string?>? Finished;      // lavoro finito; stringa = errore (null se riuscito)
    public bool Paused;                              // pausa: non parte niente di nuovo
    bool closing;                                    // uscita dall'app: i lavori interrotti restano in coda
    public string? Waiting;                          // perche' un download in coda non parte (spazio)

    public JobQueue(SynchronizationContext ui)
    {
        this.ui = ui;
        // lavori rimasti in sospeso all'ultima chiusura
        if (!Store.Demo) foreach (var j in Store.Read<List<Job>>(Store.QueuePath) ?? []) jobs.Add(j);
        if (jobs.Count > 0) Log.Info($"Resuming {jobs.Count} queued job(s)");
    }

    public IReadOnlyList<Job> Active => jobs.Where(j => j.State is JobState.Queued or JobState.Running).ToList();
    public bool IsQueued(string? channelKey, string vodId) => jobs.Any(j => j.ChannelKey == channelKey && j.VodId == vodId && j.State is JobState.Queued or JobState.Running);
    public bool Busy(JobKind kind) => jobs.Any(j => j.Kind == kind && j.State == JobState.Running);

    public void Add(Job job)
    {
        jobs.Add(job);
        Log.Info($"Queued {job.Kind}: {job.Url}{job.File}");
        Save();
        Pump();
    }

    // Screenshot del README: lavori finti, che non partono
    public void AddDemo(Job job) => jobs.Add(job);

    public void CancelJob(Job job)
    {
        if (job.State == JobState.Queued) { job.State = JobState.Canceled; Done(job, null, canceled: true); }
        else job.Cancel?.Cancel();
    }

    // Uscita: si fermano i processi ma la coda salvata resta com'e', e al prossimo avvio si riprende
    public void Shutdown()
    {
        closing = true;
        Save();
        foreach (var j in jobs.Where(j => j.State == JobState.Running)) j.Cancel?.Cancel();
        Runner.KillAll();
    }

    // Avvia i lavori in coda che possono partire
    public void Pump()
    {
        if (Paused || Store.Demo) { Changed?.Invoke(); return; }
        foreach (var job in jobs.Where(j => j.State == JobState.Queued).ToList())
        {
            if (job.Kind == JobKind.Download)
            {
                if (jobs.Count(j => j.Kind == JobKind.Download && j.State == JobState.Running) >= Math.Max(1, Store.Settings.ParallelDownloads)) continue;
                if (job.ChannelKey != null && LowSpace(Folder(job)) is string why) { Waiting = why; continue; }
            }
            else if (Busy(JobKind.Transcribe)) continue;
            Waiting = null;
            _ = Start(job);
        }
        Changed?.Invoke();
    }

    async Task Start(Job job)
    {
        job.State = JobState.Running;
        job.Cancel = new CancellationTokenSource();
        job.Progress = L("Starting…");
        Changed?.Invoke();
        string? error = null;
        try
        {
            if (job.Kind == JobKind.Download) await RunDownload(job);
            else await RunTranscription(job);
            job.State = JobState.Done;
        }
        catch (OperationCanceledException) { job.State = JobState.Canceled; }
        catch (Exception e)
        {
            job.State = JobState.Failed;
            error = e.Message;
            Log.Error($"{job.Kind} failed ({job.Display}): {e.Message}");
        }
        Done(job, error, job.State == JobState.Canceled);
    }

    void Done(Job job, string? error, bool canceled)
    {
        if (closing) return;
        jobs.Remove(job);
        if (canceled)
        {
            Log.Info($"Canceled: {job.Display}");
            DeleteWorkDir(job); // annullato a mano: via anche i file parziali
        }
        else if (error != null && job.Kind == JobKind.Download) DeleteWorkDir(job);
        Save();
        if (!canceled) Finished?.Invoke(job, error);
        Pump();
    }

    void Save() => Store.Write(Store.QueuePath, jobs.Where(j => j.State is JobState.Queued or JobState.Running).ToList());

    void Report(Job job, string progress, double percent = -1) => ui.Post(_ =>
    {
        job.Progress = progress;
        if (percent >= 0) job.Percent = percent;
        Changed?.Invoke();
    }, null);

    // ------------------------------------------------------------ download
    public static bool IsStream(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Host.EndsWith("twitch.tv") || u.Host.EndsWith("kick.com"));

    static string WorkRoot => Store.Settings.WorkFolder.Trim() != "" ? Store.Settings.WorkFolder.Trim() : Path.Combine(Path.GetTempPath(), "MediaDownloader");

    static void DeleteWorkDir(Job job)
    {
        try { if (job.WorkDir != null && Directory.Exists(job.WorkDir)) Directory.Delete(job.WorkDir, true); }
        catch (Exception e) { Log.Warn($"Can't delete {job.WorkDir}: {e.Message}"); }
    }

    // Sposta il file finito (e i suoi sottotitoli) nella cartella finale: tra dischi diversi e' una copia,
    // quindi gira in background con l'avanzamento nel menu
    async Task<string> MoveToDestination(Job job, string file, CancellationToken ct)
    {
        if (job.Destination == null) return file;
        var dest = job.Destination;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var related = Directory.GetFiles(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + "*.srt");
        Report(job, L("Moving to {0}…", Path.GetPathRoot(dest)!.TrimEnd('\\')), 100);
        await Task.Run(() =>
        {
            foreach (var srt in related)
                File.Move(srt, Path.Combine(Path.GetDirectoryName(dest)!, Path.GetFileNameWithoutExtension(dest) + srt[(file.Length - Path.GetExtension(file).Length)..]), true);
            File.Move(file, dest, true);
        }, ct);
        DeleteWorkDir(job);
        Log.Info($"Moved to {dest}");
        return dest;
    }

    public static string Folder(Job job) => job.ChannelFolder != null
        ? Path.Combine(Store.Settings.StreamFolder, job.ChannelFolder)
        : IsStream(job.Url) ? Store.Settings.StreamFolder : Store.Settings.OtherFolder;

    static string? LowSpace(string folder)
    {
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
            return free < Store.Settings.MinFreeGb * (1L << 30) ? L("Waiting: less than {0} GB free on {1}", Store.Settings.MinFreeGb, Path.GetPathRoot(folder)!) : null;
        }
        catch { return null; }
    }

    async Task RunDownload(Job job)
    {
        await Tools.EnsureBasics(s => Report(job, s), job.Cancel!.Token);
        var s = Store.Settings;
        var folder = Folder(job);
        // cartella di lavoro propria del lavoro: dopo un riavvio yt-dlp riprende i frammenti gia' scaricati
        job.WorkDir ??= Path.Combine(WorkRoot, job.Id.ToString("N")[..12]);
        Directory.CreateDirectory(job.WorkDir);
        Save();
        // Twitch/Kick scaricati a mano: una sottocartella per streamer, come per i canali seguiti
        var template = IsStream(job.Url) ? (job.ChannelFolder != null ? "" : "%(uploader,channel)s\\") + s.StreamTemplate : s.OtherTemplate;

        var args = new List<string>
        {
            "--newline", "--progress", "--no-colors", "--encoding", "utf-8", "--windows-filenames", "--no-mtime",
            "--ffmpeg-location", Tools.Dir, "-N", "8", "--no-playlist", "--embed-metadata",
            "--progress-template", "download:[MD] %(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(progress._total_bytes_str)s",
            "--print", "before_dl:[MDTITLE] %(title)s",
            "--print", "after_move:[MDFILE] %(filepath)s",
            "-P", job.WorkDir, "-o", template,
        };
        if (job.Audio)
        {
            args.AddRange(["-f", "bestaudio/best", "-x", "--embed-thumbnail"]);
            if (s.AudioFormat != "best") args.AddRange(["--audio-format", s.AudioFormat, "--audio-quality", "0"]);
        }
        else
        {
            var h = s.MaxHeight;
            args.AddRange(["-f", h > 0 ? $"bv*[height<={h}]+ba/b[height<={h}]/bv*+ba/b" : "bv*+ba/b"]);
            // mp4 per tutto: stessa dimensione di mkv, ma si riproduce ovunque (browser, telefoni, Jellyfin senza
            // rimpacchettare); Twitch e Kick arrivano gia' in mp4, quindi niente passaggi in piu'
            args.AddRange(["--merge-output-format", "mp4", "--embed-chapters"]);
        }
        if (s.CookiesFile.Trim() != "" && File.Exists(s.CookiesFile)) args.AddRange(["--cookies", s.CookiesFile]);
        args.Add(job.Url);

        string? lastError = null;
        var files = new List<string>();
        Log.Info($"Downloading {job.Url} → {folder} (working in {job.WorkDir})");
        var code = await Runner.Run(Tools.YtDlp, args, line =>
        {
            if (line.StartsWith("[MD] "))
            {
                var p = line[5..].Split('|').Select(x => x.Trim()).ToArray();
                var pct = double.TryParse(p[0].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1;
                Report(job, L("{0} · {1} · {2} left", p[0], p[1], p.Length > 2 ? p[2] : "?"), pct);
            }
            else if (line.StartsWith("[MDTITLE] ")) ui.Post(_ => { job.Title = line[10..].Trim(); Changed?.Invoke(); }, null);
            else if (line.StartsWith("[MDFILE] ")) lock (files) files.Add(line[9..].Trim());
            else if (line.StartsWith("ERROR:")) lastError = line[6..].Trim();
            else if (line.StartsWith("[Merger]") || line.StartsWith("[ExtractAudio]") || line.StartsWith("[EmbedThumbnail]") || line.StartsWith("[Fixup")
                     || line.StartsWith("[Metadata]") || line.StartsWith("[VideoRemuxer]")) Report(job, L("Finishing…"));
        }, job.Cancel.Token);
        if (code != 0 || files.Count == 0) throw new Exception(lastError ?? L("yt-dlp exited with code {0}", code));

        var file = files.Last();
        if (job.Title == "") job.Title = Path.GetFileNameWithoutExtension(file);
        if (job.ChannelKey != null && job.VodId != null) Store.MarkSeen(job.ChannelKey, [job.VodId]);
        // stesso percorso relativo (sottocartella dello streamer compresa) nella cartella finale
        job.Destination = Path.Combine(folder, Path.GetRelativePath(job.WorkDir, file));
        Log.Info($"Downloaded {file}");
        if (job.Transcribe)
        {
            // il file resta nella cartella di lavoro: Whisper e la traccia sottotitoli lavorano li', poi si sposta
            job.File = job.Destination;
            Add(new Job { Kind = JobKind.Transcribe, File = file, Title = job.Title, WorkDir = job.WorkDir, Destination = job.Destination });
            job.WorkDir = null; // ora e' della trascrizione: non va cancellata alla fine del download
        }
        else
        {
            job.File = await MoveToDestination(job, file, job.Cancel.Token);
            Store.AddHistory(job.Title, job.File, false);
        }
    }

    // ------------------------------------------------------------ trascrizione
    async Task RunTranscription(Job job)
    {
        var s = Store.Settings;
        var ct = job.Cancel!.Token;
        if (!File.Exists(job.File)) throw new Exception(L("File not found: {0}", job.File));
        await Tools.EnsureBasics(x => Report(job, x), ct);
        await Tools.EnsureWhisper(s.WhisperModel, x => Report(job, x), ct);

        // 1. audio mono a 16 kHz, il formato di Whisper
        var temp = WorkRoot;
        Directory.CreateDirectory(temp);
        var wav = Path.Combine(temp, $"{job.Id:N}.wav");
        var srtBase = Path.Combine(temp, $"{job.Id:N}");
        try
        {
            Report(job, L("Extracting audio…"), 0);
            var (code, lines) = await Runner.Capture(Tools.FFmpeg, ["-nostdin", "-y", "-loglevel", "error", "-i", job.File, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav], ct);
            if (code != 0) throw new Exception(L("ffmpeg couldn't read the audio: {0}", lines.LastOrDefault() ?? code.ToString()));

            // 2. Whisper: sottotitoli .srt, lingua riconosciuta da sola
            string language = s.Language == "auto" ? "" : s.Language;
            Report(job, L("Transcribing…"), 0);
            var langRe = new Regex(@"auto-detected language: (\w+)");
            var progRe = new Regex(@"progress\s*=\s*(\d+)%");
            var tail = new Queue<string>(); // ultime righe di whisper-cli, per capire un errore
            code = await Runner.Run(Tools.WhisperCli,
                ["-m", Tools.Model(s.WhisperModel), "-f", wav, "-l", s.Language, "-osrt", "-of", srtBase, "-pp", "-t", Math.Min(8, Environment.ProcessorCount).ToString()],
                line =>
                {
                    lock (tail) { tail.Enqueue(line); if (tail.Count > 12) tail.Dequeue(); }
                    if (langRe.Match(line) is { Success: true } m) language = m.Groups[1].Value;
                    if (progRe.Match(line) is { Success: true } p) Report(job, L("Transcribing… {0}%", p.Groups[1].Value), double.Parse(p.Groups[1].Value));
                }, ct);
            if (code != 0 || !File.Exists(srtBase + ".srt"))
            {
                Log.Error($"whisper-cli exit {code}:\n  " + string.Join("\n  ", tail));
                var reason = tail.LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("failed", StringComparison.OrdinalIgnoreCase));
                throw new Exception(L("Whisper failed (exit code {0})", code) + (reason != null ? ": " + reason.Trim() : ""));
            }

            // 3. .srt accanto al file, col codice della lingua come lo vuole Jellyfin: "video.en.srt"
            var baseName = Path.Combine(Path.GetDirectoryName(job.File)!, Path.GetFileNameWithoutExtension(job.File));
            var srt = language != "" ? $"{baseName}.{language}.srt" : $"{baseName}.srt";
            File.Move(srtBase + ".srt", srt, true);
            Log.Info($"Subtitles: {srt} (language: {(language == "" ? "?" : language)})");

            // 4. traccia sottotitoli dentro il video (senza ricodificare)
            var ext = Path.GetExtension(job.File).ToLowerInvariant();
            if (s.EmbedSubtitles && ext is ".mkv" or ".mp4" or ".m4v" or ".mov")
            {
                Report(job, L("Adding the subtitle track…"), 100);
                await Embed(job.File, srt, language, ct);
            }
            job.File = await MoveToDestination(job, job.File, ct);
            Store.AddHistory(job.Title != "" ? job.Title : Path.GetFileNameWithoutExtension(job.File), job.File, true);
        }
        catch when (!closing && job.Destination != null && File.Exists(job.File))
        {
            // sottotitoli falliti o annullati: il video scaricato va comunque al suo posto
            try
            {
                job.File = await MoveToDestination(job, job.File, CancellationToken.None);
                Store.AddHistory(job.Title, job.File, false);
            }
            catch (Exception e) { Log.Error($"Can't move {job.File}: {e.Message}"); }
            throw;
        }
        finally
        {
            try { File.Delete(wav); } catch { }
            try { File.Delete(srtBase + ".srt"); } catch { }
        }
    }

    static async Task Embed(string video, string srt, string language, CancellationToken ct)
    {
        // la nuova traccia viene dopo tutte quelle del file (video, audio, capitoli…): indice assoluto, perche'
        // contare solo i sottotitoli sbaglia quando la traccia dei capitoli dell'mp4 risulta anch'essa testo
        var (_, streams) = await Runner.Capture(Tools.FFprobe, ["-v", "error", "-show_entries", "stream=index", "-of", "csv=p=0", video], ct);
        var index = streams.Count(l => l.Trim() != "" && char.IsDigit(l.Trim()[0]));
        var ext = Path.GetExtension(video);
        var tmp = Path.Combine(Path.GetDirectoryName(video)!, Path.GetFileNameWithoutExtension(video) + ".subs-tmp" + ext);
        var args = new List<string> { "-nostdin", "-y", "-loglevel", "error", "-i", video, "-i", srt, "-map", "0", "-map", "1", "-c", "copy",
                                      "-c:s", ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ? "srt" : "mov_text" };
        if (Iso639_2(language) is string iso) args.AddRange([$"-metadata:s:{index}", $"language={iso}"]);
        args.AddRange([$"-metadata:s:{index}", "title=Whisper", tmp]);
        var (code, lines) = await Runner.Capture(Tools.FFmpeg, args, ct);
        if (code != 0)
        {
            try { File.Delete(tmp); } catch { }
            throw new Exception(L("ffmpeg couldn't add the subtitle track: {0}", lines.LastOrDefault() ?? code.ToString()));
        }
        File.Move(tmp, video, true);
    }

    // mkv e mp4 vogliono la lingua in ISO 639-2 (tre lettere)
    static string? Iso639_2(string code) => code switch
    {
        "en" => "eng", "it" => "ita", "es" => "spa", "fr" => "fre", "de" => "ger", "pt" => "por", "ru" => "rus",
        "ja" => "jpn", "ko" => "kor", "zh" => "chi", "nl" => "dut", "pl" => "pol", "tr" => "tur", "ar" => "ara",
        "sv" => "swe", "no" => "nor", "da" => "dan", "fi" => "fin", "uk" => "ukr", "cs" => "cze", "el" => "gre",
        "hu" => "hun", "ro" => "rum", "he" => "heb", "hi" => "hin", "id" => "ind", "vi" => "vie", "th" => "tha",
        _ => null,
    };
}
