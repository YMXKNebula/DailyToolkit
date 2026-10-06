using System.ComponentModel;
using System.Runtime.InteropServices;
using DailyToolkit.Desktop.Runtime;
namespace DailyToolkit.Desktop.Presentation;
public sealed partial class MainViewModel
{
    private IStartupRegistration _startupRegistration=null!;
    private bool _startAtLogin,_adminStartup,_silentStartup,_applyingStartup,_startupDisposed;
    private int _startupChangeGeneration;
    private Task _pendingStartupChange=Task.CompletedTask;
    private string _startupStatus="";
    public bool CloseToTray
    {
        get => _appPreferences.CloseToTray;
        set { _appPreferences=_appPreferences with { CloseToTray=value }; SaveAppPreferences(); Notify(); }
    }
    public bool MinimizeToTray
    {
        get => _appPreferences.MinimizeToTray;
        set { _appPreferences=_appPreferences with { MinimizeToTray=value }; SaveAppPreferences(); Notify(); }
    }
    public bool StartAtLogin
    {
        get => _startAtLogin;
        set
        {
            if (!Set(ref _startAtLogin,value)) return;
            if (!value) Set(ref _adminStartup,false,nameof(AdminStartup));
            QueueStartupChange();
        }
    }
    public bool AdminStartup { get => _adminStartup; set { if (Set(ref _adminStartup,value && StartAtLogin)) QueueStartupChange(); } }
    public bool SilentStartup { get => _silentStartup; set { if (Set(ref _silentStartup,value)) QueueStartupChange(); } }
    public bool CanEditStartup => !_applyingStartup;
    public string PrivilegeText => _startupRegistration.IsAdministrator ? "运行权限：管理员" : "运行权限：标准用户";
    public string StartupStatus { get => _startupStatus; private set { if (Set(ref _startupStatus,value)) Notify(nameof(HasStartupStatus)); } }
    public bool HasStartupStatus => !string.IsNullOrEmpty(StartupStatus);
    internal Task PendingStartupChange => _pendingStartupChange;
    public RelayCommand RunAdministratorCommand { get; private set; }=null!;
    public event Action? AdministratorRestartRequested;
    internal void ShowStartupRecovery(string message)
    { Notice=message; StartupStatus=message; Page="settings-startup"; }
    private void InitializeStartup(IStartupRegistration? registration)
    {
        _startupRegistration=registration ?? new StartupRegistration();
        _startAtLogin=_appPreferences.StartAtLogin; _adminStartup=_appPreferences.AdminStartup; _silentStartup=_appPreferences.SilentStartup;
        RunAdministratorCommand=new(_ => RequestAdministrator(),() => !_startupRegistration.IsAdministrator && CanEditStartup);
    }
    internal void RefreshStartupRegistration()
    {
        try
        {
            var actual=_startupRegistration.Read();
            _appPreferences=_appPreferences with { StartAtLogin=actual.Enabled,AdminStartup=actual.Administrator };
            _startAtLogin=actual.Enabled; _adminStartup=actual.Administrator;
            StartupStatus="";
            Notify(nameof(StartAtLogin)); Notify(nameof(AdminStartup));
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException)
        { StartupStatus="无法读取启动项："+e.Message; }
    }
    private void QueueStartupChange()
    {
        var previous=_pendingStartupChange;
        _pendingStartupChange=ApplyQueuedStartupChangeAsync(previous,++_startupChangeGeneration);
    }
    private async Task ApplyQueuedStartupChangeAsync(Task previous,int generation)
    {
        // Coalesce updates from one UI event and serialize changes across a pending UAC request.
        await Task.Yield();
        await previous;
        if (_startupDisposed || generation != _startupChangeGeneration) return;
        await ApplyStartupAsync(generation);
    }
    private void RestoreStartupChoice(StartupChoice previous,int generation)
    {
        _appPreferences=_appPreferences with { StartAtLogin=previous.Enabled,AdminStartup=previous.Administrator,SilentStartup=previous.Silent };
        if (generation != _startupChangeGeneration) return;
        _startAtLogin=previous.Enabled; _adminStartup=previous.Administrator; _silentStartup=previous.Silent;
        Notify(nameof(StartAtLogin)); Notify(nameof(AdminStartup)); Notify(nameof(SilentStartup));
    }
    private async Task ApplyStartupAsync(int generation)
    {
        _applyingStartup=true; Notify(nameof(CanEditStartup)); RunAdministratorCommand.Refresh();
        StartupStatus="正在保存启动设置…";
        var choice=new StartupChoice(StartAtLogin,AdminStartup,SilentStartup);
        var previous=new StartupChoice(_appPreferences.StartAtLogin,_appPreferences.AdminStartup,_appPreferences.SilentStartup);
        try
        {
            _appPreferences=_appPreferences with { StartAtLogin=choice.Enabled,AdminStartup=choice.Administrator,SilentStartup=choice.Silent };
            if (!_appPreferencesStore.Save(_appPreferences))
            { RestoreStartupChoice(previous,generation); StartupStatus="无法保存启动设置。"; return; }
            try
            {
                if (choice.Enabled != previous.Enabled || choice.Administrator != previous.Administrator)
                    await _startupRegistration.ApplyAsync(choice);
            }
            catch
            {
                RestoreStartupChoice(previous,generation);
                if (!_appPreferencesStore.Save(_appPreferences))
                    throw new InvalidOperationException("无法恢复启动设置的本地记录。");
                throw;
            }
            StartupStatus="";
            if (choice.Administrator && !previous.Administrator && !_startupRegistration.IsAdministrator &&
                !_startupDisposed && generation == _startupChangeGeneration) RequestAdministrator();
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { StartupStatus="已取消授权，启动设置未改变。"; }
        catch (Exception e) when (e is COMException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        { StartupStatus="无法更改启动设置："+e.Message; }
        finally
        {
            _applyingStartup=false; Notify(nameof(CanEditStartup)); RunAdministratorCommand.Refresh();
        }
    }
    private void RequestAdministrator()
    {
        try { _startupRegistration.LaunchAdministrator(requireRegisteredTask:_appPreferences.AdminStartup); AdministratorRestartRequested?.Invoke(); }
        catch (Exception e) when (e is COMException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        { StartupStatus="无法切换运行权限："+e.Message; }
    }
}
