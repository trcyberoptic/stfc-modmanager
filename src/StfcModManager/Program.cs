using System.Runtime.InteropServices;

namespace StfcModManager;

internal static partial class Program
{
    private const int AttachParentProcess = -1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            AttachConsole(AttachParentProcess);
            return SelfTest.Run();
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new Form { Text = "STFC Mod Manager", Width = 900, Height = 600 });
        return 0;
    }
}
