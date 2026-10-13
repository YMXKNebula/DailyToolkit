using System.IO;
using DailyToolkit.Desktop.ClipRecall;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Notes;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckPalettePersistenceAsync()
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-palette-");
        try
        {
            var notesStore = new NotesStore(Path.Combine(directory.FullName, "notes"));
            using (var notes = new NotesViewModel(notesStore))
            {
                await notes.Ready;
                var applied = notes.Preferences;
                notes.PreviewBackgroundColor = "#202030"; notes.PreviewFontColor = "#EECCAA";
                notes.PreviewFocusBorderColor = "#11CC88";
                notes.FontSize = 26; notes.Text = "isolated color fixture";
                Require(notes.Preferences.Background == applied.Background && notes.FontColor == applied.Font.Color &&
                    notes.FocusBorderColor == applied.FocusBorderColor && notes.HasColorChanges,
                    "Custom colors escaped the notes preview");
                Require(await notes.FlushAsync() && (await notesStore.LoadAsync()).Preferences.Font.Color == applied.Font.Color,
                    "Autosave persisted a notes color draft");
                notes.DiscardColorCommand.Execute(null);
                Require(!notes.HasColorChanges && notes.PreviewFontColor == notes.FontColor, "Discard did not restore notes preview");
                notes.PreviewFontColor = "#EECCAA"; notes.PreviewFocusBorderColor = "#11CC88"; notes.PreviewBackgroundColor = "#202030";
                notes.ResetBackground();
                Require(notes.PreviewBackgroundColor == notes.BackgroundColor && notes.PreviewFontColor == "#EECCAA" &&
                    notes.PreviewFocusBorderColor == "#11CC88", "Background reset discarded unrelated color drafts");
                notes.PreviewBackgroundColor = "#202030"; notes.ResetFont();
                Require(notes.PreviewBackgroundColor == "#202030" && notes.PreviewFocusBorderColor == "#11CC88" &&
                    notes.PreviewFontColor == notes.FontColor, "Font reset discarded unrelated color drafts");
                notes.DiscardColorDraft();
                var presets = notes.AppearancePresets;
                var first = notes.ApplyAppearancePresetAsync(presets[1].Value);
                var last = notes.ApplyAppearancePresetAsync(presets[^1].Value);
                Require(await first && await last && notes.FontColor == presets[^1].Value.Text && !notes.HasColorChanges &&
                    (await notesStore.LoadAsync()).Preferences.Background.Color == presets[^1].Value.Background,
                    "Rapid notes preset selections did not persist the last selection");
                notes.PreviewFontColor = "#EECCAA";
                var save = notes.SaveColorDraftAsync(); notes.FontSize = 31; notes.PreviewFontColor = "#CCEEAA";
                Require(await save && notes.FontColor == "#EECCAA" && notes.PreviewFontColor == "#CCEEAA" && notes.HasColorChanges &&
                    notes.FontSize == 31 && notes.Text == "isolated color fixture",
                    "Color save discarded newer draft, text or other settings");
                Require(await notes.FlushAsync(), "Notes flush failed after concurrent color/settings edits");
            }
            using (var restarted = new NotesViewModel(notesStore))
            {
                await restarted.Ready;
                Require(restarted.FontColor == "#EECCAA" && restarted.PreviewFontColor == "#EECCAA" && restarted.FontSize == 31,
                    "Restart restored an unsaved notes draft or lost committed settings");
            }
            using (var notes = new NotesViewModel(new(Path.Combine(directory.FullName, "notes-reset"))))
            {
                await notes.Ready; var defaults = notes.Preferences;
                var save = notes.ApplyAppearancePresetAsync(notes.AppearancePresets[^1].Value); notes.ResetBackground(); notes.ResetFont();
                Require(await save && notes.BackgroundColor == defaults.Background.Color && notes.FontColor == defaults.Font.Color &&
                    await notes.FlushAsync() && (await notes.Store.LoadAsync()).Preferences.Background.Color == defaults.Background.Color,
                    "Pending notes preset overrode a later partial reset to unchanged defaults");
                save = notes.ApplyAppearancePresetAsync(notes.AppearancePresets[1].Value); notes.ResetSettings();
                Require(await save && await notes.FlushAsync() && notes.Preferences == defaults &&
                    (await notes.Store.LoadAsync()).Preferences == defaults, "Pending notes preset overrode a later full reset");
            }
            var badNotesStore = new NotesStore(Path.Combine(directory.FullName, "bad-notes"));
            using (var notes = new NotesViewModel(badNotesStore))
            {
                await notes.Ready; var applied = notes.Preferences;
                Directory.CreateDirectory(badNotesStore.SettingsPath);
                notes.PreviewFontColor = "#EECCAA";
                var save = notes.SaveColorDraftAsync();
                Require(!await notes.FlushAsync(exiting: true), "Exit ignored an in-flight color save failure");
                Require(!await save && notes.Preferences == applied && notes.HasColorChanges && notes.Notice.Length > 0,
                    "Failed notes custom save changed actual colors or lost the draft");
                Require(!await notes.ApplyAppearancePresetAsync(notes.AppearancePresets[^1].Value) && notes.Preferences == applied &&
                    notes.PreviewFontColor == "#EECCAA", "Failed notes preset changed actual colors or draft");
            }
            Console.WriteLine("PASS notes preset persistence, preview-only colors, discard, restart, rapid selections, concurrent draft/settings edits and storage failure");

            var gamingStore = new GamingPreferencesStore(Path.Combine(directory.FullName, "gaming.json"));
            using (var lens = new GamingViewModel(gamingStore))
            {
                var applied = lens.BorderColor;
                lens.PreviewBorderColor = "#EECCAA"; lens.FrameWidth = 720;
                Require(lens.BorderColor == applied && gamingStore.Load().BorderColor == applied && lens.HasBorderColorChanges && !lens.IsActive,
                    "Custom lens preview changed actual or saved border colors");
                lens.DiscardBorderColorCommand.Execute(null);
                Require(!lens.HasBorderColorChanges && lens.PreviewBorderColor == applied, "Lens discard retained a draft");
                lens.BorderColor = "#4285F4";
                Require(gamingStore.Load().BorderColor == "#4285F4" && !lens.HasBorderColorChanges, "Lens preset required a separate save");
                lens.PreviewBorderColor = "#EECCAA"; lens.SaveBorderColorCommand.Execute(null);
                Require(lens.BorderColor == "#EECCAA" && gamingStore.Load().BorderColor == "#EECCAA" && !lens.HasBorderColorChanges,
                    "Lens custom save failed");
                lens.PreviewBorderColor = "#CCEEAA"; lens.BorderColor = lens.BorderColor;
                Require(!lens.HasBorderColorChanges, "Selecting the applied preset failed to discard the lens draft");
            }
            using (var restarted = new GamingViewModel(gamingStore))
                Require(restarted.BorderColor == "#EECCAA" && restarted.PreviewBorderColor == "#EECCAA", "Lens restart lost committed color");
            var badGamingPath = Path.Combine(directory.FullName, "bad-gaming.json"); Directory.CreateDirectory(badGamingPath);
            using (var lens = new GamingViewModel(new(badGamingPath)))
            {
                var applied = lens.BorderColor; lens.PreviewBorderColor = "#EECCAA";
                lens.SaveBorderColorCommand.Execute(null);
                Require(lens.BorderColor == applied && lens.PreviewBorderColor == "#EECCAA" && lens.Status.Contains("未能保存"),
                    "Failed lens custom save changed actual color or lost draft");
                lens.BorderColor = "#4285F4";
                Require(lens.BorderColor == applied && lens.PreviewBorderColor == "#EECCAA", "Failed lens preset applied unsaved colors");
            }
            Console.WriteLine("PASS lens immediate presets, preview-only custom colors, explicit save/discard, restart and storage failure without starting capture");

            var clipboardStore = new ClipboardSettingsStore(Path.Combine(directory.FullName, "clipboard"));
            await using (var clipboard = new ClipboardHistoryViewModel(clipboardStore))
            {
                await clipboard.Ready;
                clipboard.SelectAppearancePresetCommand.Execute(clipboard.AppearancePresets[1]);
                clipboard.SelectAppearancePresetCommand.Execute(clipboard.AppearancePresets[^1]);
                Require(await clipboard.PendingAppearanceSave && clipboard.Appearance.Window == clipboard.AppearancePresets[^1].Palette.Window &&
                    clipboard.AppearanceDraft.Window == clipboard.Appearance.Window && !clipboard.HasAppearanceChanges &&
                    !clipboard.IsEnabled && !File.Exists(clipboard.Repository.DatabasePath),
                    "Rapid presets lost the last selection or initialized disabled history");
                clipboard.AppearanceColors.Single(option => option.Key == "Text").Value = "#EECCAA";
                var save = clipboard.SaveAppearanceAsync(); clipboard.MaximumRecords = 4321;
                clipboard.AppearanceColors.Single(option => option.Key == "Text").Value = "#CCEEAA";
                Require(await save && clipboard.Appearance.Text == "#EECCAA" && clipboard.AppearanceDraft.Text == "#CCEEAA" &&
                    clipboard.HasAppearanceChanges && clipboard.MaximumRecords == 4321, "Clipboard color save overwrote newer draft/settings");
                clipboard.SelectAppearancePresetCommand.Execute(clipboard.AppearancePresets[2]);
                Require(await clipboard.PrepareExitAsync(), "Exit did not wait for a queued preset save");
            }
            await using (var restarted = new ClipboardHistoryViewModel(clipboardStore))
            {
                await restarted.Ready;
                Require(restarted.Appearance.Window == restarted.AppearancePresets[2].Palette.Window && restarted.MaximumRecords == 4321 &&
                    !restarted.HasAppearanceChanges, "Exit/restart lost a pending preset or restored an unsaved draft");
            }
            Console.WriteLine("PASS clipboard queued preset order, preview draft isolation, concurrent settings and exit persistence without enabling capture");
            var badClipboardStore = new ClipboardSettingsStore(Path.Combine(directory.FullName, "bad-clipboard"));
            await using (var clipboard = new ClipboardHistoryViewModel(badClipboardStore))
            {
                await clipboard.Ready; var applied = clipboard.Appearance;
                Directory.CreateDirectory(badClipboardStore.SettingsPath);
                clipboard.AppearanceColors.Single(option => option.Key == "Text").Value = "#EECCAA";
                var save = clipboard.SaveAppearanceAsync();
                Require(!await clipboard.PrepareExitAsync() && !await save && clipboard.Appearance == applied && clipboard.HasAppearanceChanges,
                    "Exit ignored a pending clipboard palette save failure or lost its draft");
                clipboard.DiscardAppearanceCommand.Execute(null);
                Require(await clipboard.PrepareExitAsync(), "An old color save failure permanently blocked exit");
            }
            Console.WriteLine("PASS pending color save failures keep drafts available and do not permanently block exit");
            await using (var clipboard = new ClipboardHistoryViewModel(new(Path.Combine(directory.FullName, "clipboard-reset"))))
            {
                await clipboard.Ready; var defaults = clipboard.Appearance;
                clipboard.SelectAppearancePresetCommand.Execute(clipboard.AppearancePresets[1]);
                var reset = clipboard.ResetSettingsAsync();
                Require(await clipboard.PendingAppearanceSave, "Superseded preset operation failed"); await reset;
                Require(clipboard.Appearance == defaults && !clipboard.HasAppearanceChanges && (await clipboard.Store.LoadAsync()).Settings.PanelColors.Count == 0,
                    "Pending clipboard preset overrode a later settings reset");
            }
            Console.WriteLine("PASS partial/full resets take precedence over earlier pending palette saves");
        }
        finally { Directory.Delete(directory.FullName, recursive: true); }
    }
}
