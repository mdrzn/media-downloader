// Capitoli per argomento: la trascrizione (.srt), riassunta a blocchi da ~60 s, va a Claude Code (in WSL)
// che risponde con i cambi di argomento; ffmpeg li scrive nel video SOSTITUENDO i capitoli di Twitch
// ("Just Chatting" per ore non serve a niente). Qualsiasi errore lascia il video com'e'.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaDownloader;

static class Chapters
{
    public sealed record Chapter(TimeSpan Start, string Title);

    static readonly TimeSpan ClaudeTimeout = TimeSpan.FromMinutes(5);

    // Aggiunge i capitoli a `video` (sostituito sul posto). `work` = cartella su disco veloce per il remux.
    // Restituisce il numero di capitoli; eccezione se qualcosa va storto (il chiamante lo registra e prosegue).
    public static async Task<int> Apply(string video, string srt, string work, Action<string> status, CancellationToken ct)
    {
        var duration = await Duration(video, ct);
        var digest = Digest(srt);
        if (digest.Count < 3) throw new Exception("transcript too short for chapters");

        status(L("Chapters: asking Claude…"));
        var chapters = Validate(await AskClaude(digest, duration, ct), duration);

        status(L("Chapters: writing {0} chapters…", chapters.Count));
        Directory.CreateDirectory(work);
        var id = Guid.NewGuid().ToString("N")[..8];
        var meta = Path.Combine(work, $"chapters-{id}.txt");
        var tmp = Path.Combine(work, $"chapters-{id}{Path.GetExtension(video)}");
        try
        {
            File.WriteAllText(meta, Metadata(chapters, duration), new UTF8Encoding(false));
            // -map 0: tutte le tracce (anche i sottotitoli incorporati) tranne quelle "dati" (la vecchia traccia
            // capitoli dell'mp4); -map_chapters 1: solo i capitoli nuovi
            var (code, lines) = await Runner.Capture(Tools.FFmpeg,
                ["-nostdin", "-y", "-loglevel", "error", "-i", video, "-i", meta, "-map", "0", "-map", "-0:d?",
                 "-map_metadata", "0", "-map_chapters", "1", "-c", "copy", tmp], ct);
            if (code != 0 || !File.Exists(tmp)) throw new Exception("ffmpeg: " + (lines.LastOrDefault() ?? $"exit {code}"));
            // sul posto: se il video e' su un altro disco (es. K:) e' una copia, quindi in background
            await Task.Run(() => File.Move(tmp, video, true), ct);
        }
        finally
        {
            try { File.Delete(meta); } catch { }
            try { File.Delete(tmp); } catch { }
        }
        return chapters.Count;
    }

    // .srt -> una riga per blocco da ~60 s: "[H:MM:SS] testo del blocco"
    public static List<string> Digest(string srtPath)
    {
        var blocks = new SortedDictionary<int, StringBuilder>();
        var time = new Regex(@"^(\d+):(\d{2}):(\d{2})[,.](\d{3})\s*-->");
        int minute = -1;
        foreach (var raw in File.ReadLines(srtPath))
        {
            var line = raw.Trim();
            if (line == "" || Regex.IsMatch(line, @"^\d+$")) continue;
            if (time.Match(line) is { Success: true } m)
            {
                minute = (int)new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)).TotalMinutes;
                continue;
            }
            if (minute < 0) continue;
            if (!blocks.TryGetValue(minute, out var sb)) blocks[minute] = sb = new StringBuilder();
            sb.Append(' ').Append(line);
        }
        return blocks.Select(b => $"[{Clock(TimeSpan.FromMinutes(b.Key))}] {Regex.Replace(b.Value.ToString(), @"\s+", " ").Trim()}").ToList();
    }

    static string Prompt(TimeSpan duration) => $$"""
        You are given the transcript of a livestream VOD, summarized as one line per ~60-second block:
        "[H:MM:SS] what was said in that minute". The video is {{Clock(duration)}} long.

        Split it into chapters by TOPIC: start a new chapter every time the streamer moves on to a different
        subject (typically every 3-15 minutes). These replace the platform's category chapters, so they must
        say what is actually being discussed.

        Rules:
        - The first chapter starts at 0:00:00.
        - No chapters shorter than about 2 minutes, unless the topic clearly changes.
        - Titles in English, short (max ~60 characters) and specific: names, companies, products, events.
          Good: "FAA grounds every airline", "Jensen-gate continues", "SBF richest-if-not-jailed list".
          Bad: "Discussion", "Chatting with chat", "Topic 3".
        - No numbering, no quotes around titles, no emoji.
        - Start times must be increasing and taken from the block timestamps.

        Reply with ONLY a JSON array, no prose and no code fences:
        [{"start":"H:MM:SS","title":"..."}]

        TRANSCRIPT:

        """;

    static async Task<List<(string start, string title)>> AskClaude(List<string> digest, TimeSpan duration, CancellationToken ct)
    {
        var s = Store.Settings;
        var args = new List<string>();
        if (s.WslDistro.Trim() != "") args.AddRange(["-d", s.WslDistro.Trim()]);
        args.AddRange(["--", "bash", "-lc", s.ChaptersCommand]);
        var input = Prompt(duration) + string.Join("\n", digest) + "\n";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ClaudeTimeout);
        (int code, string stdout, string stderr) r;
        try { r = await Runner.RunWithInput("wsl.exe", args, input, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new Exception($"Claude didn't answer within {ClaudeTimeout.TotalMinutes:0} minutes");
        }
        if (r.code != 0) throw new Exception($"claude exit {r.code}: {Last(r.stderr) ?? Last(r.stdout)}");

        // --output-format json: {"type":"result","is_error":false,"result":"<testo della risposta>",...}
        string text;
        using (var doc = JsonDocument.Parse(r.stdout))
        {
            if (doc.RootElement.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True)
                throw new Exception("claude: " + (doc.RootElement.TryGetProperty("result", out var m) ? m.GetString() : "error"));
            text = doc.RootElement.GetProperty("result").GetString() ?? "";
        }
        int a = text.IndexOf('['), b = text.LastIndexOf(']');
        if (a < 0 || b <= a) throw new Exception("no JSON array in Claude's answer");
        using var list = JsonDocument.Parse(text[a..(b + 1)]);
        return list.RootElement.EnumerateArray()
            .Select(e => (e.GetProperty("start").GetString() ?? "", e.GetProperty("title").GetString() ?? ""))
            .ToList();
    }

    // Orari validi e crescenti, entro la durata, primo a 0:00; titoli puliti
    public static List<Chapter> Validate(List<(string start, string title)> raw, TimeSpan duration)
    {
        var result = new List<Chapter>();
        foreach (var (start, title) in raw)
        {
            if (!TryParse(start, out var t)) throw new Exception($"invalid time \"{start}\"");
            var clean = Regex.Replace(title.Trim().Trim('"'), @"^\d+[.)]\s*", "");
            clean = Regex.Replace(clean, @"\s+", " ");
            if (clean == "") throw new Exception($"empty title at {start}");
            if (clean.Length > 80) clean = clean[..77].TrimEnd() + "…";
            if (t >= duration) break; // oltre la fine: si ignora il resto
            if (result.Count > 0 && t <= result[^1].Start) throw new Exception($"times not increasing at {start}");
            result.Add(new Chapter(t, clean));
        }
        if (result.Count == 0) throw new Exception("no chapters");
        if (result[0].Start != TimeSpan.Zero)
        {
            if (result[0].Start > TimeSpan.FromMinutes(5)) throw new Exception($"first chapter starts at {Clock(result[0].Start)}, not 0:00");
            result[0] = result[0] with { Start = TimeSpan.Zero };
        }
        return result;
    }

    // File FFMETADATA: un [CHAPTER] per capitolo, in millisecondi
    static string Metadata(List<Chapter> chapters, TimeSpan duration)
    {
        static string Escape(string s) => Regex.Replace(s, @"[=;#\\\n]", m => "\\" + m.Value);
        var sb = new StringBuilder(";FFMETADATA1\n");
        for (int i = 0; i < chapters.Count; i++)
        {
            var end = i + 1 < chapters.Count ? chapters[i + 1].Start : duration;
            sb.Append("[CHAPTER]\nTIMEBASE=1/1000\n")
              .Append($"START={(long)chapters[i].Start.TotalMilliseconds}\n")
              .Append($"END={(long)end.TotalMilliseconds}\n")
              .Append($"title={Escape(chapters[i].Title)}\n");
        }
        return sb.ToString();
    }

    static async Task<TimeSpan> Duration(string video, CancellationToken ct)
    {
        var (code, lines) = await Runner.Capture(Tools.FFprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", video], ct);
        // ffprobe puo' stampare avvisi prima del numero (es. "Referenced QT chapter track not found")
        foreach (var line in lines)
            if (code == 0 && double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0)
                return TimeSpan.FromSeconds(s);
        throw new Exception("can't read the video duration");
    }

    static bool TryParse(string s, out TimeSpan t) =>
        TimeSpan.TryParseExact(s.Trim(), [@"h\:mm\:ss", @"hh\:mm\:ss", @"m\:ss", @"mm\:ss"], CultureInfo.InvariantCulture, out t);

    static string Clock(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

    static string? Last(string s) => s.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l != "");

    // La trascrizione accanto al video: "video.en.srt" o "video.srt"
    public static string? FindSrt(string video)
    {
        var dir = Path.GetDirectoryName(video)!;
        var name = Path.GetFileNameWithoutExtension(video);
        return Directory.GetFiles(dir, name + "*.srt").OrderBy(f => f.Length).FirstOrDefault();
    }
}
