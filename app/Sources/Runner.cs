// Avvia gli strumenti esterni (yt-dlp, ffmpeg, whisper-cli) senza finestra, riga per riga, annullabili.
using System.Diagnostics;
using System.Text;

namespace MediaDownloader;

static class Runner
{
    static readonly HashSet<Process> running = [];

    // Uscita dall'app: chiude subito tutti i processi avviati (e i loro figli, es. l'ffmpeg di yt-dlp),
    // senza aspettare il thread dell'interfaccia, che sta per chiudersi
    public static void KillAll()
    {
        lock (running)
            foreach (var p in running)
                try { p.Kill(true); } catch { }
    }

    // Exit code; ogni riga di stdout e stderr va a onLine (da un thread in background)
    public static async Task<int> Run(string exe, IEnumerable<string> args, Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8"; // yt-dlp: titoli con accenti ed emoji
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        DataReceivedEventHandler handler = (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
        p.OutputDataReceived += handler;
        p.ErrorDataReceived += handler;
        p.Start();
        lock (running) running.Add(p);
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            try { await p.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                throw;
            }
            p.WaitForExit(); // svuota gli ultimi eventi di output
            return p.ExitCode;
        }
        finally { lock (running) running.Remove(p); }
    }

    // Con un testo sullo stdin (es. un prompt troppo lungo per la riga di comando); stdout e stderr separati
    public static async Task<(int code, string stdout, string stderr)> RunWithInput(string exe, IEnumerable<string> args, string input, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Tools.Dir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new Exception($"Can't start {exe}");
        lock (running) running.Add(p);
        try
        {
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            await p.StandardInput.WriteAsync(input.AsMemory(), ct);
            p.StandardInput.Close();
            try { await p.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                throw;
            }
            return (p.ExitCode, await stdout, await stderr);
        }
        finally { lock (running) running.Remove(p); }
    }

    // Comodo per i comandi brevi: exit code + tutte le righe
    public static async Task<(int code, List<string> lines)> Capture(string exe, IEnumerable<string> args, CancellationToken ct = default)
    {
        var lines = new List<string>();
        var code = await Run(exe, args, l => { lock (lines) lines.Add(l); }, ct);
        return (code, lines);
    }
}
