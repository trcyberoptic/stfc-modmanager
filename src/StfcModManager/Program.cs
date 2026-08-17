using System.Runtime.InteropServices;

namespace StfcModManager;

internal static partial class Program
{
    private const int AttachParentProcess = -1;
    private const int SwRestore = 9;

    // Fester, eindeutiger Name statt z. B. des Fenstertitels -- eine zufaellige Kollision mit einer
    // fremden Anwendung, die denselben simplen Namen waehlt, ist damit ausgeschlossen.
    private const string SingleInstanceMutexName = "StfcModManager-SingleInstance-9F3A2E7B-6C1B-4B92-8B62-9C8B9E7B6E1B";
    private const string MainWindowTitle = "STFC Mod Manager";

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(nint lpClassName, string lpWindowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hWnd);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            // --selftest muss unbedingt laufen, auch wenn irgendwo bereits eine GUI-Instanz offen
            // ist -- sonst wuerde ein CI-Lauf auf einer Maschine mit laufendem Manager grundlos
            // scheitern. Deshalb VOR dem Instanz-Mutex geprueft, nicht danach.
            AttachConsole(AttachParentProcess);
            return SelfTest.Run();
        }

        var restartedByUpdate = args.Contains("--restarted-by-update", StringComparer.OrdinalIgnoreCase);

        // Zwei gleichzeitig laufende Instanzen waeren ein echtes Risiko: beide schreiben potenziell
        // in denselben Spielordner, ohne voneinander zu wissen -- Installer.Apply hat keinen
        // prozessuebergreifenden Schutz (genau deshalb brauchte AppState.Save schon einen
        // pro-Prozess-eindeutigen Nebendateinamen). "restartedByUpdate" kommt von
        // SelfUpdate.ApplyAsync: die alte Instanz haelt den Mutex noch, bis ihr Application.Exit()
        // unten den Message-Loop beendet hat -- ohne kurzes Warten wuerde die frisch neugestartete
        // Instanz sich sonst faelschlich fuer eine zweite halten und sich sofort wieder beenden.
        using var singleInstance = AcquireSingleInstanceMutex(restartedByUpdate, out var acquired);
        if (!acquired)
        {
            BringExistingInstanceToFront();
            return 0;
        }

        Core.SelfUpdate.CleanupOldExecutable();
        Core.Installer.PruneBackups();

        // Fix-Runde 1, I8: ohne einen registrierten Handler zeigt WinForms fuer jede aus einem
        // Ereignishandler entkommende Ausnahme seinen eigenen Standarddialog -- roher Text plus
        // Stacktrace, genau das, was die Regel "keine Betriebssystem-/Ausnahmetexte vor dem Nutzer"
        // verbietet, und die Stelle, an der die Spec-Zeile "Unexpected error -- see support package"
        // sonst nirgends existiert. SetUnhandledExceptionMode muss VOR Application.Run stehen, sonst
        // greift der eingebaute .NET-Standard (in .NET Core/5+ anders als unter .NET Framework:
        // ThrowException statt CatchException) weiter. Zwei Handler, weil zwei verschiedene Faelle
        // entkommen koennen: Application.ThreadException faengt alles, was synchron aus einem
        // normal aufgerufenen Ereignishandler entkommt (z. B. Sha256File auf einer gesperrten DLL
        // waehrend Rescan, ein weiterwerfendes AppState.Save, Process.Start's Win32Exception in
        // "Open config" oder beim Support-Paket-Reveal) -- AppDomain.UnhandledException zusaetzlich
        // fuer eine Ausnahme aus einem async-void-Ereignishandler (z. B. OnCheckUpdates), die dort
        // nicht mehr ankommt; der Prozess beendet sich danach trotzdem (dafuer gibt es in .NET keinen
        // Ausweg), aber der Nutzer sieht wenigstens eine erklaerende Meldung statt eines
        // kommentarlosen Absturzes, und die Einzelheiten stehen im Log.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => HandleUnhandledException(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            HandleUnhandledException(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject.ToString()));

        ApplicationConfiguration.Initialize();
        Application.Run(new Ui.MainForm());
        return 0;
    }

    /// <summary>Letztes Auffangnetz (s. Aufrufer-Kommentar): protokolliert die volle Ausnahme und
    /// zeigt dem Nutzer nur eine feste, englische, betriebssystemfreie Zeile -- dieselbe Regel wie
    /// ueberall sonst in der Ui-Schicht, hier nur fuer den Fall, dass irgendetwas sie umgangen hat.</summary>
    private static void HandleUnhandledException(Exception e)
    {
        Core.AppLog.Error("unhandled exception reached the UI message loop", e);
        MessageBox.Show(
            "An unexpected error occurred. Generate a support package and check the manager log for details.",
            "Unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>Normalfall: Timeout 0, exakt das Verhalten von "new Mutex(true, name, out
    /// createdNew)" -- eine bereits laufende, ECHTE zweite Instanz wird sofort erkannt. Nach einem
    /// Selbst-Update-Neustart (waitForPreviousInstance) wird stattdessen kurz gewartet, bis die
    /// alte Instanz tatsaechlich beendet ist.</summary>
    private static Mutex AcquireSingleInstanceMutex(bool waitForPreviousInstance, out bool acquired)
    {
        var mutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);
        try
        {
            acquired = mutex.WaitOne(waitForPreviousInstance ? TimeSpan.FromSeconds(5) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // Die Vorgaengerinstanz wurde beendet (Application.Exit -> Prozessende), ohne
            // ReleaseMutex aufzurufen -- ganz normal bei einem Prozessende, kein Hinweis auf einen
            // Absturz. .NET gewaehrt den Mutex dem wartenden Thread trotzdem, meldet das aber ueber
            // eine Exception statt ueber den Rueckgabewert. Genau der erwartete Weg direkt nach
            // einem Selbst-Update-Neustart.
            acquired = true;
        }
        return mutex;
    }

    /// <summary>Best effort: holt das Fenster der bereits laufenden Instanz nach vorne. Gelingt das
    /// nicht (FindWindow findet nichts, weil z. B. der Fenstertitel zwischenzeitlich geaendert
    /// wurde), zeigt stattdessen eine kurze Meldung -- niemals ein stiller, kommentarloser Exit ohne
    /// jede Rueckmeldung an den Nutzer.</summary>
    private static void BringExistingInstanceToFront()
    {
        var hWnd = FindWindowW(0, MainWindowTitle);
        if (hWnd != 0)
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);
            SetForegroundWindow(hWnd);
            return;
        }

        MessageBox.Show(
            "STFC Mod Manager is already running.",
            "STFC Mod Manager",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
