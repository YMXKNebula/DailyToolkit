using System.IO;
using System.Text.Json;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static void CheckRenamedSettingsCompatibility()
    {
        var directory=Directory.CreateTempSubdirectory("DailyToolkit-settings-tests-");
        try
        {
            var legacy=Path.Combine(directory.FullName,"DailyUSE");
            Directory.CreateDirectory(legacy);
            var saved=new GamingPreferences
            {
                ToggleShortcut=new(3,0x5A,"Ctrl + Alt + Z"),ActivationMode=LensActivationMode.Hold,
                MovementMode=LensMovementMode.Movable,WheelZoomEnabled=false
            };
            var oldGaming=Path.Combine(legacy,"gaming.json");
            var oldFavorites=Path.Combine(legacy,"favorites.json");
            File.WriteAllText(oldGaming,JsonSerializer.Serialize(saved));
            File.WriteAllText(oldFavorites,"[\"screen-lens\",\"future-tool\"]");
            var gaming=new GamingPreferencesStore(localDataDirectory:directory.FullName);
            var favorites=new FavoritesStore(localDataDirectory:directory.FullName);
            Require(gaming.Load() == saved && favorites.Load().SetEquals(["screen-lens","future-tool"]),
                "Renamed app lost legacy shortcuts, modes, wheel settings or favorites");
            var replacement=saved with { ToggleShortcut=null,MovementMode=LensMovementMode.Fixed };
            Require(gaming.Save(replacement) && favorites.Save(["future-tool"]),"Renamed settings could not be saved");
            Require(File.Exists(Path.Combine(directory.FullName,"DailyToolkit","gaming.json")) &&
                File.Exists(Path.Combine(directory.FullName,"DailyToolkit","favorites.json")),"Settings used the old product directory");
            Require(gaming.Load() == replacement && favorites.Load().SetEquals(["future-tool"]),
                "Old settings overwrote newer choices");
            Require(new GamingPreferencesStore(oldGaming).Load() == saved &&
                new FavoritesStore(oldFavorites).Load().SetEquals(["screen-lens","future-tool"]),
                "Compatibility loading modified original settings");
            File.WriteAllText(Path.Combine(directory.FullName,"DailyToolkit","gaming.json"),"{");
            Require(gaming.Load() == new GamingPreferences(),"Broken new settings resurrected old choices");
            Console.WriteLine("PASS Product rename preserves old settings and favorites, saves under the new name and respects newer choices");
        }
        finally
        {
            var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if (Path.GetFullPath(directory.FullName).StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase) &&
                directory.Name.StartsWith("DailyToolkit-settings-tests-",StringComparison.Ordinal)) directory.Delete(recursive:true);
        }
    }
}
