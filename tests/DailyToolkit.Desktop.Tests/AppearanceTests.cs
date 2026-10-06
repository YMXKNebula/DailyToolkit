using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;
using DailyToolkit.Core.Environment;
using DailyToolkit.Desktop.Controls;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckThemeEditingAsync()
    {
        var directory=Directory.CreateTempSubdirectory("DailyToolkit-theme-tests-");
        var display=new DisplayInfo(1920,1080,1,1920,1040,false);
        var store=new AppPreferencesStore(Path.Combine(directory.FullName,"settings.json"));
        MainViewModel Create(AppPreferencesStore preferences)
        {
            var probe=new ControlledProbe(display); probe.Finish.TrySetResult();
            return new(probe,display,new LocalProbe(),
                gamingPreferencesStore:new GamingPreferencesStore(Path.Combine(directory.FullName,"gaming.json")),
                favoritesStore:new FavoritesStore(Path.Combine(directory.FullName,"favorites.json")),
                navigationStore:new NavigationOrderStore(Path.Combine(directory.FullName,"navigation.json")),
                appPreferencesStore:preferences,startupRegistration:new StartupFake());
        }
        var model=Create(store); model.Page="settings-theme";
        var window=new MainWindow(model,enableShortcuts:false) { ShowActivated=false,ShowInTaskbar=false,
            WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000 };
        try
        {
            window.Show(); window.UpdateLayout();
            var details=(Expander)window.FindName("ThemeDetails");
            var preview=(ThemePreview)window.FindName("PalettePreview");
            var presetList=(ItemsControl)window.FindName("ThemePresetList");
            var save=(Button)window.FindName("SaveThemeButton");
            async Task ClickCommandAsync(Button button)
            {
                var peer=new ButtonAutomationPeer(button);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                await Application.Current.Dispatcher.InvokeAsync(() => { },DispatcherPriority.ContextIdle);
            }
            IEnumerable<DependencyObject> Descendants(DependencyObject parent)
            {
                for (var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
                {
                    var child=VisualTreeHelper.GetChild(parent,i); yield return child;
                    foreach (var descendant in Descendants(child)) yield return descendant;
                }
            }
            var presetButtons=Descendants(presetList).OfType<Button>().ToArray();
            Require(!details.IsExpanded && model.ThemePresets.Count == 8 && presetButtons.Length == 8 && !save.IsEnabled,
                "Theme presets were missing or detailed colors were shown without opening their section");
            var blue=model.ThemePresets.Single(p => p.Name == "海蓝");
            await ClickCommandAsync(presetButtons.Single(button => button.DataContext == blue));
            window.UpdateLayout();
            Require(model.Theme == blue.Palette && preview.Palette == blue.Palette && store.Load().Theme == blue.Palette &&
                model.ThemePresets.Single(p => p.IsSelected) == blue && !model.HasThemeChanges,
                "Selecting a real preset button did not apply and persist the whole palette");
            var originalFile=File.ReadAllText(Path.Combine(directory.FullName,"settings.json"));
            details.IsExpanded=true; window.UpdateLayout();
            var picker=Descendants(details).OfType<ColorPicker>().Single(control => control.DataContext is ThemeColorOption { Key:"Window" });
            Descendants(picker).OfType<Button>().Single(button => button.Tag as string == "#202020")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Require(model.PreviewTheme.Window == "#202020" && preview.Palette == model.PreviewTheme && model.Theme == blue.Palette &&
                model.HasThemeChanges && save.IsEnabled && File.ReadAllText(Path.Combine(directory.FullName,"settings.json")) == originalFile,
                "A detailed color changed the live theme or saved settings before Save");
            model.AnimationColor="#123456"; window.UpdateLayout();
            Require(((SolidColorBrush)window.FindResource("WindowBrush")).Color.ToString() == "#FF"+blue.Palette.Window[1..] &&
                store.Load().Theme == blue.Palette && model.HasThemeChanges,
                "An unrelated appearance update applied or persisted the unsaved palette");
            using (var reopened=Create(store)) Require(reopened.Theme == blue.Palette && reopened.PreviewTheme == blue.Palette,
                "An unsaved preview survived as the applied palette in a new instance");
            model.Page="home"; model.Page="settings-theme"; window.UpdateLayout();
            Require(model.Theme == blue.Palette && model.PreviewTheme.Window == "#202020",
                "Navigating away from the editor applied or lost its draft");
            await ClickCommandAsync(save); window.UpdateLayout();
            Require(model.Theme.Window == "#202020" && store.Load().Theme == model.Theme && !model.HasThemeChanges &&
                ((SolidColorBrush)window.FindResource("WindowBrush")).Color.ToString() == "#FF202020" && !save.IsEnabled &&
                !model.ThemePresets.Any(preset => preset.IsSelected),"Save did not apply the draft or retained the old preset selection");
            model.ThemeColors.Single(option => option.Key == "Text").Value="#654321";
            model.DiscardThemeCommand.Execute(null);
            Require(model.PreviewTheme == model.Theme && !model.HasThemeChanges,"Discard did not restore the applied colors");
            model.ResetThemePreviewCommand.Execute(null);
            Require(model.PreviewTheme == new ThemePalette() && model.Theme.Window == "#202020" && model.HasThemeChanges,
                "Restoring preview defaults changed the applied theme before Save");
            model.SelectThemePresetCommand.Execute(blue);
            Require(model.Theme == blue.Palette && model.PreviewTheme == blue.Palette && !model.HasThemeChanges,
                "Selecting a preset did not replace an unsaved draft");
            var blocked=Path.Combine(directory.FullName,"blocked"); File.WriteAllText(blocked,"file");
            using (var failed=Create(new AppPreferencesStore(Path.Combine(blocked,"settings.json"))))
            {
                failed.ThemeColors.Single(option => option.Key == "Window").Value="#123456";
                failed.SaveThemeCommand.Execute(null);
                Require(failed.Theme == new ThemePalette() && failed.PreviewTheme.Window == "#123456" &&
                    failed.HasThemeChanges && failed.HasNotice,"A failed Save applied the draft or discarded it");
            }
            double Luminance(string hex)
            {
                var color=(Color)ColorConverter.ConvertFromString(hex);
                double Linear(byte value) { var c=value/255d; return c <= .04045 ? c/12.92 : Math.Pow((c+.055)/1.055,2.4); }
                return .2126*Linear(color.R)+.7152*Linear(color.G)+.0722*Linear(color.B);
            }
            double Contrast(string a,string b)
            { var x=Luminance(a); var y=Luminance(b); return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05); }
            foreach (var preset in model.ThemePresets)
            {
                var p=preset.Palette;
                Require(p == p.Normalize() && Contrast(p.Text,p.Window)>=4.5 && Contrast(p.Muted,p.Surface)>=4.5 &&
                    Contrast(p.AccentForeground,p.Accent)>=4.5 && Contrast(p.Accent,p.AccentSoft)>=4.5,
                    $"Preset colors were invalid or had low text contrast: {preset.Name}");
            }
        }
        finally
        {
            window.Close(); await model.PendingWork;
            var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if (Path.GetFullPath(directory.FullName).StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase) &&
                directory.Name.StartsWith("DailyToolkit-theme-tests-",StringComparison.Ordinal)) directory.Delete(recursive:true);
        }
        Console.WriteLine("PASS Eight theme presets apply directly; detailed colors affect only preview until Save, with discard, restart isolation and failed-save recovery");
    }
}
