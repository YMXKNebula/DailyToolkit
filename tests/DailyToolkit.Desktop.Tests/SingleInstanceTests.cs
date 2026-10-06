using DailyToolkit.Desktop;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckSingleInstanceAsync()
    {
        var name=$"Local\\DailyToolkit.Tests.{Guid.NewGuid():N}";
        using (var primary=new SingleInstance(name))
        {
            Require(primary.IsPrimary,"The first app did not acquire its instance guard");
            await Task.Run(() =>
            {
                using var secondary=new SingleInstance(name);
                Require(!secondary.IsPrimary,"A duplicate app could own a different set of shortcuts and settings");
            });
        }
        using (var reopened=new SingleInstance(name)) Require(reopened.IsPrimary,"Closing the app did not release its guard");
        var abandonedName=$"Local\\DailyToolkit.Tests.{Guid.NewGuid():N}";
        Mutex? abandoned=null;
        var thread=new Thread(() => { abandoned=new Mutex(false,abandonedName); abandoned.WaitOne(); });
        thread.Start(); thread.Join();
        using (abandoned)
        using (var recovered=new SingleInstance(abandonedName)) Require(recovered.IsPrimary,"An abandoned instance blocked restart");
        Console.WriteLine("PASS Duplicate apps cannot own independent settings or shortcuts; normal close and abandoned owners allow restart");
    }
}
