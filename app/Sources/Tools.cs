// Strumenti esterni in %LOCALAPPDATA%\MediaDownloader\tools, scaricati al primo uso e tenuti aggiornati:
//   yt-dlp (download), ffmpeg/ffprobe (unione, estrazione audio, sottotitoli incorporati),
//   whisper-cli di whisper.cpp + modello ggml (trascrizione; build CUDA se c'e' una GPU NVIDIA).
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaDownloader;

static class Tools
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaDownloader", "tools");
    public static string YtDlp => Path.Combine(Dir, "yt-dlp.exe");
    public static string FFmpeg => Path.Combine(Dir, "ffmpeg.exe");
    public static string FFprobe => Path.Combine(Dir, "ffprobe.exe");
    public static string WhisperCli => Path.Combine(Dir, "whisper", "whisper-cli.exe");
    public static string Model(string name) => Path.Combine(Dir, "models", $"ggml-{name}.bin");
    public static readonly string[] Models = ["large-v3-turbo", "large-v3"];

    static readonly HttpClient http = CreateClient();
    static readonly SemaphoreSlim gate = new(1, 1); // un'installazione alla volta
    public static bool HasNvidiaGpu => File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"));
    public static bool BasicsReady => File.Exists(YtDlp) && File.Exists(FFmpeg) && File.Exists(FFprobe);
    public static bool WhisperReady(string model) => File.Exists(WhisperCli) && File.Exists(Model(model));

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MediaDownloader", Application.ProductVersion.Split('+')[0]));
        return c;
    }

    // yt-dlp + ffmpeg (~200 MB): servono per qualsiasi download
    public static async Task EnsureBasics(Action<string> status, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Dir);
            if (!File.Exists(YtDlp))
                await Download("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", YtDlp, "yt-dlp", status, ct);
            if (!File.Exists(FFmpeg) || !File.Exists(FFprobe))
            {
                // la build di ffmpeg che yt-dlp stesso consiglia
                var zip = Path.Combine(Dir, "ffmpeg.zip");
                await Download("https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip", zip, "ffmpeg", status, ct);
                Extract(zip, Dir, n => n.EndsWith("bin/ffmpeg.exe") || n.EndsWith("bin/ffprobe.exe"));
                File.Delete(zip);
            }
        }
        finally { gate.Release(); }
    }

    // whisper-cli (~1,1 GB con le librerie CUDA) + modello (~1,6 GB turbo, ~3 GB large-v3): solo quando serve
    public static async Task EnsureWhisper(string model, Action<string> status, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(WhisperCli))
            {
                var url = await WhisperZipUrl(ct);
                var zip = Path.Combine(Dir, "whisper.zip");
                await Download(url, zip, "Whisper", status, ct);
                var dir = Path.GetDirectoryName(WhisperCli)!;
                Directory.CreateDirectory(dir);
                // solo il necessario: whisper-cli e le DLL (niente esempi e test)
                Extract(zip, dir, n => n.EndsWith("/whisper-cli.exe", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
                File.Delete(zip);
            }
            if (!File.Exists(Model(model)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Model(model))!);
                await Download($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{model}.bin", Model(model), $"Whisper {model}", status, ct);
            }
        }
        finally { gate.Release(); }
    }

    // Ultima release di whisper.cpp con i binari Windows x64: CUDA 12 se c'e' una GPU NVIDIA, altrimenti CPU
    static async Task<string> WhisperZipUrl(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=15", ct));
        var cuda = new Regex(@"^whisper-(bin-win-cuda|cublas)-12[\d.]*(-bin)?-x64\.zip$", RegexOptions.IgnoreCase);
        var cpu = new Regex(@"^whisper-bin-x64\.zip$", RegexOptions.IgnoreCase);
        foreach (var pattern in HasNvidiaGpu ? new[] { cuda, cpu } : [cpu])
            foreach (var release in doc.RootElement.EnumerateArray())
                foreach (var asset in release.GetProperty("assets").EnumerateArray())
                    if (pattern.IsMatch(asset.GetProperty("name").GetString() ?? ""))
                        return asset.GetProperty("browser_download_url").GetString()!;
        throw new Exception("No Windows build of whisper.cpp found in the latest releases.");
    }

    // I siti cambiano spesso: yt-dlp si aggiorna da solo una volta al giorno
    public static async Task UpdateYtDlp(bool force = false)
    {
        var stamp = Path.Combine(Dir, "yt-dlp.updated");
        if (!force && File.Exists(stamp) && DateTime.Now - File.GetLastWriteTime(stamp) < TimeSpan.FromHours(20)) return;
        if (!File.Exists(YtDlp)) return;
        await gate.WaitAsync();
        try
        {
            var (code, lines) = await Runner.Capture(YtDlp, ["-U"]);
            Log.Info($"yt-dlp -U: {lines.LastOrDefault(l => l.Trim() != "")?.Trim()} (exit {code})");
            File.WriteAllText(stamp, DateTime.Now.ToString("s"));
        }
        catch (Exception e) { Log.Warn($"yt-dlp update failed: {e.Message}"); }
        finally { gate.Release(); }
    }

    public static async Task<string> YtDlpVersion()
    {
        if (!File.Exists(YtDlp)) return "";
        try { return (await Runner.Capture(YtDlp, ["--version"])).lines.FirstOrDefault()?.Trim() ?? ""; }
        catch { return ""; }
    }

    // Reinstalla tutto da capo (pulsante nelle impostazioni)
    public static void DeleteBinaries()
    {
        foreach (var f in new[] { YtDlp, FFmpeg, FFprobe }) if (File.Exists(f)) File.Delete(f);
        var w = Path.GetDirectoryName(WhisperCli)!;
        if (Directory.Exists(w)) Directory.Delete(w, true);
    }

    static async Task Download(string url, string path, string label, Action<string> status, CancellationToken ct)
    {
        Log.Info($"Downloading {label} from {url}");
        var tmp = path + ".part";
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(tmp);
            var buffer = new byte[1 << 20];
            long done = 0;
            var last = DateTime.MinValue;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (DateTime.Now - last > TimeSpan.FromMilliseconds(500))
                {
                    last = DateTime.Now;
                    status(total > 0 ? L("Installing {0}: {1:0}% of {2}", label, done * 100.0 / total, Size(total)) : L("Installing {0}: {1}", label, Size(done)));
                }
            }
        }
        File.Move(tmp, path, true);
        Log.Info($"Installed {label} ({Size(new FileInfo(path).Length)})");
    }

    static void Extract(string zip, string dir, Func<string, bool> keep)
    {
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
            if (entry.Length > 0 && keep(entry.FullName.Replace('\\', '/')))
                entry.ExtractToFile(Path.Combine(dir, entry.Name), true);
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };
}
