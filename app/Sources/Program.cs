// Avvio: una sola istanza per utente; i comandi da riga di comando vanno all'istanza gia' avviata.
//   MediaDownloader.exe [--download <url> | --audio <url> | --transcribe <file> | --chapters <file|cartella> | --check | --latest <canale> | --pause | --resume | --settings | --quit]
//   --lang en|it             forza la lingua
//   --render-docs <cartella> dati finti: salva menu.png, settings.png e states.png per il README ed esce
//   --export-icon <file.ico> scrive l'icona dell'app
global using static MediaDownloader.Strings;

namespace MediaDownloader;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string? command = null, docs = null;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i].ToLowerInvariant())
            {
                case "--lang": Strings.Italian = Next() == "it"; break;
                case "--export-icon": Icons.ExportAppIcon(Next()); return 0;
                case "--render-docs": docs = Next(); Store.Demo = true; break;
                case "--download": command = "download\t" + Next(); break;
                case "--audio": command = "audio\t" + Next(); break;
                case "--transcribe": command = "transcribe\t" + Path.GetFullPath(Next()); break;
                case "--check": command = "check"; break;
                case "--chapters": command = "chapters\t" + Path.GetFullPath(Next()); break;
                case "--latest": command = "latest\t" + Next(); break;
                case "--pause": command = "pause"; break;
                case "--resume": command = "resume"; break;
                case "--settings": command = "settings"; break;
                case "--menu": command = "menu"; break;
                case "--quit": command = "quit"; break;
                default:
                    // un link o un file passato da solo (es. "Apri con")
                    if (!args[i].StartsWith("--")) command = File.Exists(args[i]) ? "transcribe\t" + Path.GetFullPath(args[i]) : "ask\t" + args[i];
                    break;
            }
        }

        using var mutex = new Mutex(true, Store.Demo ? @"Local\MediaDownloader.Demo" : @"Local\MediaDownloader", out bool first);
        if (!first)
        {
            Ipc.Send(command ?? "settings");
            return 0;
        }
        if (command == "quit") return 0;

        ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001 // tema chiaro/scuro di Windows (API ancora "experimental" in .NET 10)
        Application.SetColorMode(SystemColorMode.System);
#pragma warning restore WFO5001

        var app = new TrayApp();
        Ipc.Listen(app.Run);
        if (docs != null) app.RenderDocs(docs);
        else if (command != null) app.Run(command);
        Application.Run(app);
        return 0;
    }
}
