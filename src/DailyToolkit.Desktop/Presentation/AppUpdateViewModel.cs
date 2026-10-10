using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using DailyToolkit.Desktop.Runtime;
using Microsoft.Win32;

namespace DailyToolkit.Desktop.Presentation;

internal sealed class AppUpdateViewModel : ObservableObject, IDisposable
{
    private readonly AppUpdateService _service;
    private readonly string? _installation;
    private CancellationTokenSource? _operation;
    private AppUpdate? _update;
    private string _status = "", _downloaded = "";
    private bool _busy, _installing, _disposed, _downloading;
    private double _progress;

    internal AppUpdateViewModel(AppUpdateService? service = null, string? installation = null)
    { _service = service ?? new(); _installation = installation ?? UpdateInstallation.CurrentDirectory(); }

    public string ActionText => _update is null ? "检查更新" : "更新";
    public string Status { get => _status; private set { if (Set(ref _status, value)) Notify(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(Status);
    public bool IsBusy => _busy;
    public bool CanUpdate => !_busy;
    public bool CanCancel => _busy && !_installing;
    public bool IsIndeterminate => !_downloading;
    public bool HasDownload => !string.IsNullOrEmpty(_downloaded);
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    internal async Task RunAsync(System.Windows.Window? owner = null)
    {
        if (_busy || _disposed) return;
        _busy = true;
        var operation = new CancellationTokenSource();
        _operation = operation;
        NotifyState();
        try
        {
            if (_update is null)
            {
                Status = "正在检查更新…";
                _update = await _service.CheckAsync(Version.Parse(MainViewModel.ApplicationVersion), _installation is not null, operation.Token);
                Status = _update is null ? "已是最新版本。" : "发现新版本 " + _update.Version.ToString(3);
                Notify(nameof(ActionText));
                return;
            }
            string destination;
            if (_installation is null)
            {
                var dialog = new SaveFileDialog { Title = "保存新版软件", FileName = _update.FileName,
                    Filter = "ZIP 压缩包 (*.zip)|*.zip", DefaultExt = ".zip", AddExtension = true };
                if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true)
                { Status = "已取消下载。"; return; }
                destination = dialog.FileName;
            }
            else
            {
                var directory = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "DailyToolkit", "Updates", _update.Version.ToString(3));
                Directory.CreateDirectory(directory);
                destination = Path.Combine(directory, _update.FileName);
            }
            _downloading = true; Progress = 0; Notify(nameof(IsIndeterminate)); Status = "正在下载更新…";
            var progress = new Progress<double>(value => { if (!_disposed && ReferenceEquals(_operation, operation)) Progress = value; });
            await _service.DownloadAsync(_update, destination, progress, operation.Token);
            if (_installation is null)
            {
                _downloaded = destination; Notify(nameof(HasDownload)); Status = "新版已下载。";
                _update = null; Notify(nameof(ActionText));
            }
            else
            {
                if (UpdateInstallation.CurrentDirectory() != _installation)
                    throw new InvalidOperationException("安装位置已改变，请重新打开软件后更新。");
                operation.Token.ThrowIfCancellationRequested();
                _installing = true; NotifyState(); Status = "正在安装更新，完成后会重新打开软件。";
                using var installer = Process.Start(UpdateInstallation.InstallerStart(destination, _installation)) ??
                    throw new InvalidOperationException("没有启动更新程序，请重试。");
                await installer.WaitForExitAsync(operation.Token);
                if (installer.ExitCode != 0) throw new InvalidOperationException("更新未完成。请保存笔记并退出软件后重试。");
            }
        }
        catch (OperationCanceledException)
        { if (!_disposed) Status = operation.IsCancellationRequested ? "已取消更新。" : "连接超时，请稍后再试。"; }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { Status = "已取消更新。"; }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or
            JsonException or InvalidOperationException or Win32Exception or KeyNotFoundException or ArgumentException)
        {
            Trace.WriteLine(error);
            if (!_disposed) Status = error is InvalidDataException or InvalidOperationException ? error.Message :
                error is HttpRequestException ? "无法连接更新服务，请稍后再试。" :
                error is IOException or UnauthorizedAccessException ? "无法保存更新文件，请检查保存位置后重试。" : "无法读取更新信息，请稍后再试。";
        }
        finally
        {
            _operation = null; operation.Dispose(); _busy = false; _installing = false; _downloading = false;
            if (!_disposed) NotifyState();
        }
    }

    internal void Cancel() { if (CanCancel) _operation?.Cancel(); }
    internal void OpenDownload()
    {
        if (!File.Exists(_downloaded)) { Status = "下载文件已移动或删除。"; return; }
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add("/select," + _downloaded);
            using var process = Process.Start(start);
        }
        catch (Win32Exception error) { Trace.WriteLine(error); Status = "没有打开文件夹。"; }
    }

    private void NotifyState()
    {
        Notify(nameof(IsBusy)); Notify(nameof(CanUpdate)); Notify(nameof(CanCancel)); Notify(nameof(IsIndeterminate));
    }

    public void Dispose()
    { if (_disposed) return; _disposed = true; _operation?.Cancel(); _service.Dispose(); }
}
