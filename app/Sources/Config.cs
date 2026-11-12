// Impostazioni in %APPDATA%\MediaDownloader\config.json; stato (cronologia, VOD gia' visti, coda) accanto.
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaDownloader;

enum Platform { Twitch, Kick }

// Un canale seguito: i VOD nuovi si scaricano da soli
sealed class Channel
{
    public Platform Platform { get; set; }
    public string Name { get; set; } = "";       // nome nell'URL: twitch.tv/<name>, kick.com/<name>
    public string Folder { get; set; } = "";     // sottocartella in StreamFolder (vuota = Name)
    public bool Audio { get; set; }              // solo audio
    public bool Transcribe { get; set; } = true;
    public bool Enabled { get; set; } = true;
    [JsonIgnore] public string Key => $"{Platform}:{Name}".ToLowerInvariant();
    [JsonIgnore] public string FolderName => Folder.Trim() == "" ? Name : Folder.Trim();
    [JsonIgnore] public string VideosUrl => Platform == Platform.Twitch
        ? $"https://www.twitch.tv/{Name}/videos?filter=archives"
        : $"https://kick.com/{Name}/videos";
    [JsonIgnore] public string LiveUrl => Platform == Platform.Twitch ? $"https://www.twitch.tv/{Name}" : $"https://kick.com/{Name}";
    public Channel Clone() => (Channel)MemberwiseClone();
}

sealed class Settings
{
    // Twitch/Kick: <StreamFolder>\<canale>\<StreamTemplate>; tutto il resto: <OtherFolder>\<OtherTemplate>
    public string StreamFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Streaming");
    public string OtherFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public string StreamTemplate { get; set; } = "%(upload_date>%Y-%m-%d)s - %(title).150B [%(id)s].%(ext)s";
    public string OtherTemplate { get; set; } = "%(title).150B [%(id)s].%(ext)s";
    public int MaxHeight { get; set; }                       // 0 = la migliore disponibile
    public string AudioFormat { get; set; } = "mp3";         // mp3, m4a, opus, best (originale)
    public string CookiesFile { get; set; } = "";
    public string WorkFolder { get; set; } = "";             // disco veloce per scaricare ed elaborare; vuota = cartella temporanea
    public int ParallelDownloads { get; set; } = 2;

    public List<Channel> Channels { get; set; } = [];
    public int CheckMinutes { get; set; } = 30;
    public int MinFreeGb { get; set; } = 20;                 // sotto questa soglia i download automatici aspettano

    public bool TranscribeVideo { get; set; } = true;        // predefinito per i download a mano
    public bool TranscribeAudio { get; set; }
    public bool EmbedSubtitles { get; set; } = true;
    public string WhisperModel { get; set; } = "large-v3-turbo";
    public string Language { get; set; } = "auto";

    public bool OfferCopiedLinks { get; set; } = true;
    public string Hotkey { get; set; } = "Ctrl+Alt+D";
    public bool NotifySuccess { get; set; } = true;
}

sealed record HistoryItem(string Title, string File, DateTime Date, bool Subtitles);

static class Store
{
    public static bool Demo;
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaDownloader");
    static string ConfigPath => Path.Combine(Dir, "config.json");
    static string HistoryPath => Path.Combine(Dir, "history.json");
    static string SeenPath => Path.Combine(Dir, "seen.json");
    public static string QueuePath => Path.Combine(Dir, "queue.json");

    public static Settings Settings = new();
    public static List<HistoryItem> History = [];
    public static Dictionary<string, HashSet<string>> Seen = [];   // VOD gia' scaricati (o esistenti quando il canale e' stato aggiunto)
    public static string? LoadError;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static void Load()
    {
        if (Demo) { LoadDemo(); return; }
        Directory.CreateDirectory(Dir);
        try
        {
            if (File.Exists(ConfigPath)) Settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(ConfigPath), Json) ?? new();
            else Save(); // primo avvio: scrive i valori predefiniti, cosi' il file si puo' anche modificare a mano
        }
        catch (Exception e)
        {
            LoadError = e.Message;
            Log.Error($"Can't read {ConfigPath}: {e.Message}");
        }
        History = Read<List<HistoryItem>>(HistoryPath) ?? [];
        Seen = Read<Dictionary<string, HashSet<string>>>(SeenPath) ?? [];
    }

    public static void Save()
    {
        if (Demo) return;
        if (LoadError != null && File.Exists(ConfigPath))
            File.Copy(ConfigPath, Path.Combine(Dir, "config.broken.json"), true); // non perdere un config scritto a mano
        Write(ConfigPath, Settings);
        LoadError = null;
    }

    public static void AddHistory(string title, string file, bool subtitles)
    {
        History.RemoveAll(h => h.File.Equals(file, StringComparison.OrdinalIgnoreCase));
        History.Insert(0, new HistoryItem(title, file, DateTime.Now, subtitles));
        if (History.Count > 30) History.RemoveRange(30, History.Count - 30);
        Write(HistoryPath, History);
    }

    public static void MarkSeen(string channelKey, IEnumerable<string> ids)
    {
        if (!Seen.TryGetValue(channelKey, out var set)) Seen[channelKey] = set = [];
        set.UnionWith(ids);
        Write(SeenPath, Seen);
    }

    public static T? Read<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default; }
        catch (Exception e) { Log.Warn($"Can't read {path}: {e.Message}"); return default; }
    }

    // Scrittura atomica: file temporaneo + rinomina
    public static void Write<T>(string path, T value)
    {
        if (Demo) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception e) { Log.Error($"Can't save {path}: {e.Message}"); }
    }

    // Dati finti per gli screenshot del README
    static void LoadDemo()
    {
        Settings = new Settings
        {
            StreamFolder = @"D:\Media\Streaming",
            OtherFolder = @"D:\Downloads",
            Channels =
            [
                new() { Platform = Platform.Twitch, Name = "somestreamer" },
                new() { Platform = Platform.Kick, Name = "anotherone", Audio = true },
            ],
        };
        var now = DateTime.Now;
        History =
        [
            new("Late night chill stream", @"D:\Media\Streaming\somestreamer\2026-11-11 - Late night chill stream [v123].mkv", now.AddMinutes(-12), true),
            new("Live set — summer festival", @"D:\Downloads\Live set — summer festival [abc].mp3", now.AddHours(-2), false),
            new("How the web works in 10 minutes", @"D:\Downloads\How the web works in 10 minutes [xyz].mp4", now.AddHours(-5), true),
        ];
    }
}
