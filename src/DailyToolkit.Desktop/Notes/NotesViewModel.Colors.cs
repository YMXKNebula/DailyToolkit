using DailyToolkit.Core.Notes;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Notes;

public sealed partial class NotesViewModel
{
    private NotesColors? _colorDraft;
    private long _backgroundColorRevision, _textColorRevision, _borderColorRevision;
    private Task<bool> _colorSave = Task.FromResult(true);
    internal Task<bool> PendingColorSave => _colorSave;
    private static NotesColors ReadColors(NotesPreferences value) => new(value.Background.Color, value.Font.Color, value.FocusBorderColor);
    private static NotesPreferences WithColors(NotesPreferences value, NotesColors colors) => value with
    {
        Background = value.Background with { Color = colors.Background },
        Font = value.Font with { Color = colors.Text }, FocusBorderColor = colors.Border
    };
    private NotesColors PreviewColors => _colorDraft ?? ReadColors(Preferences);
    public NotesPreferences PreviewPreferences => WithColors(Preferences, PreviewColors);
    public bool HasColorChanges => PreviewColors != ReadColors(Preferences);
    public string PreviewBackgroundColor { get => PreviewColors.Background; set => ChangeColorDraft(PreviewColors with { Background = value }); }
    public string PreviewFontColor { get => PreviewColors.Text; set => ChangeColorDraft(PreviewColors with { Text = value }); }
    public string PreviewFocusBorderColor { get => PreviewColors.Border; set => ChangeColorDraft(PreviewColors with { Border = value }); }
    public RelayCommand SaveColorCommand { get; }
    public RelayCommand DiscardColorCommand { get; }
    private void ChangeColorDraft(NotesColors colors)
    {
        if (!NotesPreferences.ValidColor(colors.Background) || !NotesPreferences.ValidColor(colors.Text) || !NotesPreferences.ValidColor(colors.Border)) return;
        _colorDraft = new(colors.Background.ToUpperInvariant(), colors.Text.ToUpperInvariant(), colors.Border.ToUpperInvariant());
        NotifyColorDraft();
    }
    private void NotifyColorDraft()
    {
        foreach (var property in new[] { nameof(PreviewPreferences), nameof(PreviewBackgroundColor), nameof(PreviewFontColor), nameof(PreviewFocusBorderColor), nameof(HasColorChanges) }) Notify(property);
        SaveColorCommand.Refresh(); DiscardColorCommand.Refresh();
    }
    public void DiscardColorDraft() { _colorDraft = null; NotifyColorDraft(); }
    internal Task<bool> SaveColorDraftAsync() => QueueColorSave(PreviewColors, preset: false);
    internal Task<bool> ApplyAppearancePresetAsync(NotesColors colors) => QueueColorSave(colors, preset: true);
    private Task<bool> QueueColorSave(NotesColors colors, bool preset)
    {
        if (_disposed) return Task.FromResult(false);
        var operation = CommitColorsAsync(_colorSave, colors, preset, PreviewColors,
            (_backgroundColorRevision, _textColorRevision, _borderColorRevision));
        _colorSave = operation;
        return operation;
    }
    private async Task<bool> CommitColorsAsync(Task<bool> previous, NotesColors colors, bool preset, NotesColors requestedDraft,
        (long Background, long Text, long Border) requestedRevisions)
    {
        await previous; await Ready; await _saving.WaitAsync();
        try
        {
            if (_disposed) return false;
            if (!_settingsWritable) { Notice = "配色未能保存，请检查原设置或恢复默认设置。"; return false; }
            var before = Preferences;
            var revision = _settingsRevision;
            var beforeColors = ReadColors(before);
            var next = WithColors(before, new(
                requestedRevisions.Background == _backgroundColorRevision ? colors.Background : beforeColors.Background,
                requestedRevisions.Text == _textColorRevision ? colors.Text : beforeColors.Text,
                requestedRevisions.Border == _borderColorRevision ? colors.Border : beforeColors.Border)).Normalize();
            await Store.SaveSettingsAsync(next);
            var changedDuringSave = _settingsRevision != revision;
            var currentColors = ReadColors(Preferences); var savedColors = ReadColors(next);
            _preferences = WithColors(Preferences, new(
                requestedRevisions.Background != _backgroundColorRevision ? currentColors.Background : savedColors.Background,
                requestedRevisions.Text != _textColorRevision ? currentColors.Text : savedColors.Text,
                requestedRevisions.Border != _borderColorRevision ? currentColors.Border : savedColors.Border));
            _settingsRevision++; _settingsDirty = changedDuringSave || ReadColors(_preferences) != savedColors;
            if ((preset && PreviewColors == requestedDraft) || PreviewColors == colors) _colorDraft = null;
            NotifyColorDraft(); Notify(""); Notice = "";
            if (_settingsDirty) ScheduleSave();
            return true;
        }
        catch (Exception exception) when (NotesStore.IsStorageError(exception))
        { Notice = "配色未能保存，请检查空间或权限。"; return false; }
        finally { _saving.Release(); }
    }
}
