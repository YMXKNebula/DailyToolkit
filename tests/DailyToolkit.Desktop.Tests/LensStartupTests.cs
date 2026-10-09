using System.Reflection;
using System.Windows.Threading;
using DailyToolkit.Desktop.Gaming;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    // Control completion/event order without requesting capture permission or a GPU.
    // These invoke the production callbacks; they do not replace their state checks.
    private static async Task CheckLensStartupTimingAsync()
    {
        using var session = new ScreenLensSession(nativePointer: false);
        var type = typeof(ScreenLensSession);
        void Set(string name, object value) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, value);
        object? Invoke(string name, params object[] args) => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, args);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        Set("_active", true); Set("_generation", 1); Set("_startupTimer", timer);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startup = (Task)Invoke("StartCaptureAsync", pending.Task, 1)!;
        Invoke("OnFirstFrame", 1);
        Require(session.IsVisible && !timer.IsEnabled, "First frame did not make the session visible and stop its timeout");
        pending.SetResult(); await startup;
        Require(!timer.IsEnabled, "First frame before start completion incorrectly restarted the startup timeout");
        Invoke("OnStartupTimeout", 1);
        Require(session.IsActive, "A timeout callback stopped a session that already received its first frame");

        DispatcherTimer Begin(int generation, bool visible = false)
        {
            session.Stop();
            var next = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            Set("_active", true); Set("_visible", visible); Set("_generation", generation); Set("_startupTimer", next);
            return next;
        }
        var normal = Begin(3);
        await (Task)Invoke("StartCaptureAsync", Task.CompletedTask, 3)!;
        Require(normal.IsEnabled, "Startup without a first frame lost its timeout");
        Invoke("OnFirstFrame", 3);
        Require(session.IsVisible && !normal.IsEnabled, "Normal first frame did not stop its timeout");
        Invoke("OnStartupTimeout", 3);
        Require(session.IsActive, "Queued timeout stopped a ready session");

        var stopped = Begin(5);
        var oldStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldStartup = (Task)Invoke("StartCaptureAsync", oldStart.Task, 5)!;
        var replacement = Begin(7);
        oldStart.SetResult(); await oldStartup;
        Invoke("OnFirstFrame", 5); Invoke("OnStartupTimeout", 5);
        Require(session.IsActive && !session.IsVisible && !replacement.IsEnabled && !stopped.IsEnabled,
            "Stopped capture's completion/frame/timeout modified its replacement");
        await (Task)Invoke("StartCaptureAsync", Task.CompletedTask, 7)!;
        Require(replacement.IsEnabled, "Replacement capture did not start its own timeout");
        Invoke("OnStartupTimeout", 7);
        Require(!session.IsActive && !session.HasCaptureResources && !replacement.IsEnabled,
            "Current capture timeout did not release the session");

        Begin(9);
        var oldFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedStartup = (Task)Invoke("StartCaptureAsync", oldFailure.Task, 9)!;
        var nextTimer = Begin(11);
        oldFailure.SetException(new InvalidOperationException("simulated stale start failure")); await failedStartup;
        Require(session.IsActive && !nextTimer.IsEnabled, "Old startup failure stopped its replacement");

        var relay = Begin(13, visible: true);
        Set("_firstFrameGeneration", 11);
        await (Task)Invoke("StartCaptureAsync", Task.CompletedTask, 13)!;
        Require(session.IsVisible && relay.IsEnabled, "Old visible frame incorrectly bypassed the new relay timeout");
        Invoke("OnFirstFrame", 11); Invoke("OnStartupTimeout", 11);
        Require(relay.IsEnabled && session.IsActive, "Old relay callbacks stopped the new capture's timer");
        Invoke("OnStartupTimeout", 13);
        Require(!session.IsActive && !session.HasCaptureResources, "Relay with no new frame did not time out");

        var fastRelay = Begin(15, visible: true);
        Set("_firstFrameGeneration", 13);
        var relayStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var relayStartup = (Task)Invoke("StartCaptureAsync", relayStart.Task, 15)!;
        Invoke("OnFirstFrame", 15); relayStart.SetResult(); await relayStartup;
        Require(session.IsActive && !fastRelay.IsEnabled, "Relay's early first frame restarted its timeout");

        Begin(17);
        await (Task)Invoke("StartCaptureAsync", Task.FromException(new InvalidOperationException("simulated current start failure")), 17)!;
        Require(!session.IsActive && !session.HasCaptureResources, "Current startup failure did not release the session");
        Console.WriteLine("PASS Lens deterministic early/normal first frame, queued timeout, stop/restart, stale completion/failure and relay readiness");
    }
}
