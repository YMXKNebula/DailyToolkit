using System.ComponentModel;
using System.Runtime.InteropServices;
using DailyToolkit.Desktop.Runtime;
namespace DailyToolkit.Desktop.Presentation;
public sealed partial class MainViewModel
{
    private IStartupRegistration _startupRegistration=null!;
    private bool _startAtLogin,_adminStartup,_silentStartup,_applyingStartup;
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
    public bool StartAtLogin { get => _startAtLogin; set { if (Set(ref _startAtLogin,value)) { if (!value) AdminStartup=false; Notify(nameof(StartupDirty)); } } }
    public bool AdminStartup { get => _adminStartup; set { if (Set(ref _adminStartup,value && StartAtLogin)) Notify(nameof(StartupDirty)); } }
    public bool SilentStartup { get => _silentStartup; set { if (Set(ref _silentStartup,value)) Notify(nameof(StartupDirty)); } }
    public bool StartupDirty => StartAtLogin != _appPreferences.StartAtLogin || AdminStartup != _appPreferences.AdminStartup || SilentStartup != _appPreferences.SilentStartup;
    public bool CanEditStartup => !_applyingStartup;
    public string PrivilegeText => _startupRegistration.IsAdministrator ? "当前：管理员运行（全部功能）" : "当前：普通权限运行";
    public string StartupStatus { get; private set; }="点击应用后生效。";
    public RelayCommand ApplyStartupCommand { get; private set; }=null!;
    public RelayCommand RunAdministratorCommand { get; private set; }=null!;
    public event Action? AdministratorRestartRequested;
    internal void ShowStartupRecovery(string message)
    { Notice=message; StartupStatus=message; Notify(nameof(StartupStatus)); }
    private void InitializeStartup(IStartupRegistration? registration)
    {
        _startupRegistration=registration ?? new StartupRegistration();
        _startAtLogin=_appPreferences.StartAtLogin; _adminStartup=_appPreferences.AdminStartup; _silentStartup=_appPreferences.SilentStartup;
        ApplyStartupCommand=new(_ => _=ApplyStartupAsync(),() => CanEditStartup);
        RunAdministratorCommand=new(_ => RequestAdministrator(),() => !_startupRegistration.IsAdministrator && CanEditStartup);
    }
    internal void RefreshStartupRegistration()
    {
        try
        {
            var actual=_startupRegistration.Read();
            _appPreferences=_appPreferences with { StartAtLogin=actual.Enabled,AdminStartup=actual.Administrator };
            _startAtLogin=actual.Enabled; _adminStartup=actual.Administrator;
            StartupStatus=actual.Enabled ? (actual.Administrator ? "管理员开机自启已启用。" : "开机自启已启用。") : "开机自启未启用。";
            Notify(nameof(StartAtLogin)); Notify(nameof(AdminStartup)); Notify(nameof(StartupDirty)); Notify(nameof(StartupStatus));
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException)
        { StartupStatus="未能读取启动项："+e.Message; Notify(nameof(StartupStatus)); }
    }
    internal async Task ApplyStartupAsync()
    {
        if (_applyingStartup) return;
        _applyingStartup=true; Notify(nameof(CanEditStartup)); ApplyStartupCommand.Refresh(); RunAdministratorCommand.Refresh();
        var choice=new StartupChoice(StartAtLogin,AdminStartup,SilentStartup);
        try
        {
            var previous=_appPreferences;
            _appPreferences=_appPreferences with { StartAtLogin=choice.Enabled,AdminStartup=choice.Administrator,SilentStartup=choice.Silent };
            if (!SaveAppPreferences()) { _appPreferences=previous; return; }
            try { await _startupRegistration.ApplyAsync(choice); }
            catch { _appPreferences=previous; SaveAppPreferences(); throw; }
            StartupStatus=choice.Enabled ? (choice.Administrator ? "管理员开机自启已启用。" : "开机自启已启用。") : "开机自启已关闭。";
            if (choice.Administrator && !_startupRegistration.IsAdministrator) RequestAdministrator();
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { StartupStatus="已取消授权，启动设置未改变。"; }
        catch (Exception e) when (e is COMException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        { StartupStatus="设置未能应用："+e.Message; }
        finally
        {
            _applyingStartup=false; Notify(nameof(CanEditStartup)); Notify(nameof(StartupDirty)); Notify(nameof(StartupStatus));
            ApplyStartupCommand.Refresh(); RunAdministratorCommand.Refresh();
        }
    }
    private void RequestAdministrator()
    {
        try { _startupRegistration.LaunchAdministrator(requireRegisteredTask:_appPreferences.AdminStartup); AdministratorRestartRequested?.Invoke(); }
        catch (Exception e) when (e is COMException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        { StartupStatus="未能切换管理员权限："+e.Message; Notify(nameof(StartupStatus)); }
    }
}
