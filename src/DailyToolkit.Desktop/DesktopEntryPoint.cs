using System.Runtime.CompilerServices;
using DailyToolkit.Desktop.FilePack.DropHost;

namespace DailyToolkit.Desktop;

internal static class DesktopEntryPoint
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == Conversion.NativeMediaWorker.Argument)
        { System.Environment.ExitCode = Conversion.NativeMediaWorker.Run(args); return; }
        if (args.Length > 0 && args[0] == FilePackDropProcess.Argument)
        { System.Environment.ExitCode = FilePackDropHostEntry.Run(args); return; }
        RunDesktop();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunDesktop() => App.Main();
}
