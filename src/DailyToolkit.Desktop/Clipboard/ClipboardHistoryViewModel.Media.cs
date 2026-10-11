using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using DailyToolkit.Core.Clipboard;

namespace DailyToolkit.Desktop.ClipRecall;

public sealed partial class ClipboardHistoryViewModel
{
    private readonly Dictionary<Guid, BitmapSource> _thumbnails = [];
    private readonly LinkedList<Guid> _thumbnailOrder = [];
    private bool _hasFileCache, _expanded;
    private BitmapSource? _selectedImage;
    private Task _imageTask = Task.CompletedTask;
    private int _imageRevision;
    private Guid? _previewId;
    private CancellationTokenSource? _imageCancellation;
    private readonly SemaphoreSlim _previewGate = new(1);
    public bool UseCachedFiles { get => _settings.UseCachedFiles; set => ChangeSettings(_settings with { UseCachedFiles = value }); }
    public int MaximumImageMegabytes { get => _settings.MaximumImageMegabytes; set => ChangeSettings(_settings with { MaximumImageMegabytes = value }); }
    public int MaximumCachedFileMegabytes { get => _settings.MaximumCachedFileMegabytes; set => ChangeSettings(_settings with { MaximumCachedFileMegabytes = value }); }
    public int MaximumFileCacheMegabytes { get => _settings.MaximumFileCacheMegabytes; set => ChangeSettings(_settings with { MaximumFileCacheMegabytes = value }); }
    public bool HasImage => _detail?.Content.Type == ClipboardContentType.Image;
    public bool HasFiles => _detail?.Content.Type == ClipboardContentType.Files;
    public bool CanDuplicateText => HasSelection && !HasImage;
    public bool HasFileCache { get => _hasFileCache; private set { if (Set(ref _hasFileCache, value)) Notify(nameof(FileCacheText)); } }
    public string FileCacheText => !HasFiles ? "" : HasFileCache ?
        "已有加密文件缓存 · 开启文件夹缓存时优先从缓存复制" : "仅保存原路径 · 文件仍存在时可以手动缓存";
    public BitmapSource? SelectedImage { get => _selectedImage; private set => Set(ref _selectedImage, value); }
    public bool IsPreviewExpanded
    {
        get => _expanded;
        set { if (Set(ref _expanded, value)) RequestImagePreview(); }
    }
    internal int ThumbnailCount => _thumbnails.Count;
    private void NotifyMedia()
    {
        Notify(nameof(HasImage)); Notify(nameof(HasFiles)); Notify(nameof(CanDuplicateText)); Notify(nameof(FileCacheText));
        RequestImagePreview();
    }
    private void RequestImagePreview()
    {
        if (!IsPreviewExpanded || !HasImage || _selected is not { } selected)
        { _imageCancellation?.Cancel(); ++_imageRevision; _previewId = null; SelectedImage = null; return; }
        if (_previewId == selected.Id) return;
        _imageCancellation?.Cancel(); _imageCancellation?.Dispose();
        _imageCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _previewId = selected.Id; var revision = ++_imageRevision; SelectedImage = null;
        _imageTask = LoadImagePreviewAsync(selected.Id, revision, _imageCancellation.Token);
    }
    private async Task LoadImagePreviewAsync(Guid id, int revision, CancellationToken token)
    {
        try
        {
            await _previewGate.WaitAsync(token);
            try
            {
                var record = await Repository.GetAsync(id, token); token.ThrowIfCancellationRequested();
                if (record?.Content.Binary is not { } png) return;
                var preview = await Task.Run(() => ClipboardImageCodec.Preview(png), token);
                token.ThrowIfCancellationRequested();
                if (!_disposed && revision == _imageRevision && IsVisible) SelectedImage = preview;
            }
            finally { _previewGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception))
        { if (!_disposed && revision == _imageRevision) Notice = "图片预览未能读取，原数据保留。"; }
    }
    internal async Task<BitmapSource?> ThumbnailAsync(Guid id)
    {
        if (_disposed || !IsVisible) return null;
        if (_thumbnails.TryGetValue(id, out var cached))
        { _thumbnailOrder.Remove(id); _thumbnailOrder.AddLast(id); return cached; }
        var png = await Repository.GetThumbnailAsync(id, _lifetime.Token);
        if (png is null) return null;
        BitmapSource small;
        try { small = await Task.Run(() => ClipboardImageCodec.Preview(png, 160), _lifetime.Token); }
        finally { CryptographicOperations.ZeroMemory(png); }
        if (_disposed || !IsVisible) return null;
        // The entire page may contain images, but only realized rows request them.
        _thumbnails[id] = small; _thumbnailOrder.Remove(id); _thumbnailOrder.AddLast(id);
        while (_thumbnails.Count > 32)
        { var old = _thumbnailOrder.First!.Value; _thumbnailOrder.RemoveFirst(); _thumbnails.Remove(old); }
        return small;
    }
    private void ReleaseMedia()
    { _imageCancellation?.Cancel(); ++_imageRevision; _previewId = null; SelectedImage = null; _thumbnails.Clear(); _thumbnailOrder.Clear(); }
    public Task<bool> CacheSelectedFilesAsync()
    {
        if (_selected is not { } selected || !HasFiles) return Task.FromResult(false);
        return ActionAsync(async () =>
        {
            await Repository.CacheFilesAsync(selected.Id, Settings, _lifetime.Token);
            if (_selected?.Id == selected.Id) HasFileCache = true;
            Notice = "文件已加密缓存。开启文件夹缓存时将从缓存复制。";
        });
    }
    public Task<bool> RemoveSelectedFileCacheAsync()
    {
        if (_selected is not { } selected) return Task.FromResult(false);
        return ActionAsync(async () =>
        {
            await Repository.RemoveFileCacheAsync(selected.Id);
            if (_selected?.Id == selected.Id) HasFileCache = false;
            Notice = "已移除加密缓存，原路径记录保留；已复制的临时文件将在最后使用 24 小时后清理。";
        });
    }
    private async Task<ClipboardHistoryRecord> PrepareFileRestoreAsync(ClipboardHistoryRecord record)
    {
        if (record.Content.Type != ClipboardContentType.Files) return record;
        if (UseCachedFiles && await Repository.HasFileCacheAsync(record.Id))
            return record with { Content = ClipboardContent.FromFiles(await Repository.MaterializeCachedFilesAsync(record.Id, Settings)) };
        var missing = await Task.Run(() => record.Content.Paths.Any(path => !File.Exists(path) && !Directory.Exists(path)));
        if (missing)
            throw new InvalidOperationException("原文件或文件夹已不存在，且没有可用缓存，未复制。删除后无法再从旧路径建立缓存。");
        if (UseCachedFiles) Notice = "此记录没有缓存，将从仍存在的原路径复制。";
        return record;
    }
}
