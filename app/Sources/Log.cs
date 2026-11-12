// Log semplice in %APPDATA%\MediaDownloader\downloader.log (oltre 2 MB passa a downloader.old.log)
namespace MediaDownloader;

static class Log
{
    static readonly object gate = new();
    public static string File => Path.Combine(Store.Dir, "downloader.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    static void Write(string level, string message)
    {
        if (Store.Demo) return;
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Store.Dir);
                var f = new FileInfo(File);
                if (f.Exists && f.Length > 2_000_000) System.IO.File.Move(File, Path.Combine(Store.Dir, "downloader.old.log"), true);
                System.IO.File.AppendAllText(File, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level,-5} {message}{Environment.NewLine}");
            }
            catch { /* il log non deve mai fermare l'app */ }
        }
    }
}
