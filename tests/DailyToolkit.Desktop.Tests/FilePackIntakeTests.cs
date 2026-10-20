using System.IO;
using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DailyToolkit.Core.FilePack;
using DailyToolkit.Desktop.FilePack;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckFilePackIntakeAsync()
    {
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-filepack-intake-");
        try
        {
            using var host = ResourceHost(fixture.FullName);
            var model = host.FilePack; await model.SettingsReady;
            Require(model.SetShortcut(null, []), "Intake fixture could not disable its global shortcut");
            using var source = ClipboardTestWindow();
            using var controller = new FilePackController(host, source, () => [], new FakeNotesForeground(),
                () => { var area = DailyToolkit.Desktop.Notes.NotesPositionService.WorkAreas()[0]; return ((int)area.X + 400, (int)area.Y + 200); });
            await controller.Ready;
            controller.ToggleQuick();
            await WaitForFilePackAsync(() => controller.QuickWindow?.IsVisible == true, "Intake quick window did not open");
            var quick = controller.QuickWindow!; quick.UpdateLayout();
            var view = (FilePackQuickView)((ContentControl)quick.FindName("Body")).Content;
            var files = (ListBox)view.FindName("FilesList");
            var input = Path.Combine(fixture.FullName, "拖入 中文.txt");
            await File.WriteAllTextAsync(input, "drag source retained");
            var folder = Directory.CreateDirectory(Path.Combine(fixture.FullName, "拖入文件夹"));
            await File.WriteAllTextAsync(Path.Combine(folder.FullName, "inside.txt"), "folder contents");
            var target = files.PointToScreen(new(files.ActualWidth / 2, files.ActualHeight / 2));
            RequireFilePackClientHit(quick, target);
            FilePackOleDrop(quick, target, [input, folder.FullName]);
            await model.PendingOperations;
            Require(model.Entries.Count == 2 && model.Entries.Any(row => row.SourcePath == input) &&
                model.Entries.Any(row => row.SourcePath == Path.Combine(folder.FullName, "inside.txt")),
                "OLE drag/drop did not collect the file and folder through the actual quick view handlers: " + model.Notice);
            var second = Path.Combine(fixture.FullName, "row-target.txt"); await File.WriteAllTextAsync(second, "second source");
            quick.UpdateLayout();
            var item = (ListBoxItem)files.ItemContainerGenerator.ContainerFromIndex(0);
            var row = FilePackDescendants(item).OfType<TextBlock>().First();
            target = row.PointToScreen(new(row.ActualWidth / 2, row.ActualHeight / 2));
            FilePackOleDrop(quick, target, [second]); await model.PendingOperations;
            Require(model.Entries.Count == 3, "Dropping onto an existing quick row lost files");

            var pipe = "DailyToolkit.FilePack.Tests." + Guid.NewGuid().ToString("N");
            var shell = Path.Combine(fixture.FullName, "右键.txt"); await File.WriteAllTextAsync(shell, "shell source");
            var errors = new ConcurrentQueue<string>();
            await using (var broker = new FilePackRequestBroker(paths => Application.Current.Dispatcher
                .InvokeAsync(() => controller.AddShellPathsAsync(paths)).Task.Unwrap(), pipe, errors.Enqueue))
            {
                Require(await FilePackRequestBroker.SendAsync([shell], pipe), "Same-user shell paths were not acknowledged");
                await WaitForFilePackAsync(() => model.Entries.Any(row => row.SourcePath == shell), "An acknowledged shell path did not reach the shared draft");
                Require(errors.Count == 0, "Shell intake failed: " + string.Join("; ", errors));
            }

            model.ShortcutWindow = FilePackShortcutWindow.Detailed; await model.PendingOperations;
            controller.ToggleShortcutWindow();
            await WaitForFilePackAsync(() => controller.PackWindow?.IsVisible == true, "The selected detailed shortcut window did not open");
            var detail = controller.PackWindow!; detail.UpdateLayout();
            var details = (FilePackView)((ContentControl)detail.FindName("Body")).Content;
            var grid = (DataGrid)details.FindName("EntriesGrid");
            var detailInput = Path.Combine(fixture.FullName, "detail-drop.txt"); await File.WriteAllTextAsync(detailInput, "detailed source");
            target = grid.PointToScreen(new(grid.ActualWidth / 2, Math.Min(100, grid.ActualHeight / 2)));
            RequireFilePackClientHit(detail, target);
            FilePackOleDrop(detail, target, [detailInput]); await model.PendingOperations;
            Require(model.Entries.Count == 5, "Detailed OLE drop did not use the shared draft: " + model.Notice);
            controller.ToggleShortcutWindow();
            await WaitForFilePackAsync(() => controller.PackWindow is null, "The selected detailed shortcut window did not close");
            Require(await File.ReadAllTextAsync(input) == "drag source retained" && await File.ReadAllTextAsync(shell) == "shell source",
                "Intake changed a source file");
            await model.ShutdownAsync();
            CheckFilePackPipeIdentity();
            await CheckFilePackBrokerAsync();
            Console.WriteLine("PASS FilePack OLE enter/over/drop at empty, populated and detailed surfaces; same-user/session shell IPC reaches shared draft; selected shortcut toggles detailed window; source files retained. No physical input, Explorer, UAC or registry changes.");
        }
        finally { FilePackPaths.EnsureNoReparsePoints(fixture.FullName); fixture.Delete(recursive: true); }
    }

    private static void CheckFilePackPipeIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = FilePackRequestBroker.CreatePipeSecurity();
        Require(security.GetOwner(typeof(SecurityIdentifier)) == identity.User && security.AreAccessRulesProtected,
            "FilePack pipe security did not bind its owner to the actual user");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        Require(rules.Length == 1 && rules[0].IdentityReference == identity.User && rules[0].AccessControlType == AccessControlType.Allow,
            "FilePack pipe permissions admitted another identity or inherited access");
    }

    private static void RequireFilePackClientHit(FilePackWindow window, Point screen)
    {
        var packed = ((uint)(int)screen.X & 0xffff) | (((uint)(int)screen.Y & 0xffff) << 16);
        Require(ClipboardChromeHit(window.Handle, 0x84, IntPtr.Zero, new IntPtr(unchecked((int)packed))).ToInt32() == 1,
            "The FilePack collection surface was treated as a caption instead of a client drop target");
    }

    // Calls WPF's own OLE target on this test's private window station. Its real
    // hit testing and routed handlers run without moving the user's pointer or
    // entering a system drag loop. Reflection is confined to this regression.
    private static void FilePackOleDrop(FilePackWindow window, Point screen, string[] paths)
    {
        var type = typeof(DragDrop).Assembly.GetType("System.Windows.OleDropTarget", throwOnError: true)!;
        var target = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, [window.Handle], culture: null)!;
        var data = new DataObject(DataFormats.FileDrop, paths);
        var point = unchecked((long)(((ulong)(uint)(int)screen.Y << 32) | (uint)(int)screen.X));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object[] enter = [data, 0, point, (int)(DragDropEffects.Copy | DragDropEffects.Move)];
        type.GetMethods(flags).Single(method => method.Name.EndsWith(".OleDragEnter", StringComparison.Ordinal)).Invoke(target, enter);
        Require((DragDropEffects)enter[3] == DragDropEffects.Copy, $"FilePack file OLE DragEnter at {screen}: expected Copy, actual {(DragDropEffects)enter[3]}");
        object[] over = [0, point, (int)(DragDropEffects.Copy | DragDropEffects.Move)];
        type.GetMethods(flags).Single(method => method.Name.EndsWith(".OleDragOver", StringComparison.Ordinal)).Invoke(target, over);
        Require((DragDropEffects)over[2] == DragDropEffects.Copy, $"FilePack file OLE DragOver at {screen}: expected Copy, actual {(DragDropEffects)over[2]}");
        object[] drop = [data, 0, point, (int)(DragDropEffects.Copy | DragDropEffects.Move)];
        type.GetMethods(flags).Single(method => method.Name.EndsWith(".OleDrop", StringComparison.Ordinal)).Invoke(target, drop);
        Require((DragDropEffects)drop[3] == DragDropEffects.Copy, $"FilePack file OLE Drop at {screen}: expected Copy, actual {(DragDropEffects)drop[3]}");
    }
}
