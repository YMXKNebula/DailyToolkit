using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Gaming;
using Vortice.DXGI;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static void CheckRendererRepeatedDispose()
    {
        var renderer = new LensGpuRenderer(IntPtr.Zero, 64, 64, software: true);
        try
        {
            renderer.Dispose(); renderer.Dispose();
            Require(renderer.Device.NativePointer == IntPtr.Zero, "Renderer disposal retained the device wrapper");
        }
        finally { if (renderer.Device.NativePointer != IntPtr.Zero) renderer.Dispose(); }
        Console.WriteLine("PASS Renderer repeated disposal after native resources have been released");
    }

    // Opt-in real WGC/driver stress. Counters include the generated test scene.
    private static async Task CheckLensResourceStabilityAsync(string output)
    {
        var monitors = LensNativeWindow.Monitors(); var monitor = monitors[0];
        using var scene = new NativeAnimatedLensFixture(monitor);
        using var session = new ScreenLensSession();
        session.SetWheelZoom(false);
        IDXGIAdapter3? adapter = null;
        var snapshots = new List<object>(); var watch = Stopwatch.StartNew();
        static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        async Task Wait(Func<bool> ready, string reason)
        {
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(10);
            Require(ready(), reason);
        }
        void Released()
        {
            Require(!session.IsActive && !session.HasCaptureResources, "Stop retained capture resources");
            var windows = (IDictionary)typeof(LensNativeWindow).GetField("Windows", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Require(windows.Count == 0, "Destroyed lens stayed rooted in its native window table");
        }
        void Snapshot(int cycles)
        {
            Collect(); using var process = Process.GetCurrentProcess(); process.Refresh();
            snapshots.Add(new { Cycles = cycles, ElapsedSeconds = watch.Elapsed.TotalSeconds, Handles = process.HandleCount,
                UserObjects = ReviewGuiResources(process.Handle, 1), GdiObjects = ReviewGuiResources(process.Handle, 0),
                PrivateBytes = process.PrivateMemorySize64, WorkingSetBytes = process.WorkingSet64, ManagedBytes = GC.GetTotalMemory(false),
                LocalVideoMemoryBytes = adapter?.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage,
                NonLocalVideoMemoryBytes = adapter?.QueryVideoMemoryInfo(0, MemorySegmentGroup.NonLocal).CurrentUsage });
        }
        try
        {
            for (var i = 0; i < 44; i++)
            {
                session.SetImageMode(LensImageMode.HighQuality);
                session.Start(monitors, monitor, 640, 384, 4, 1, 0);
                await Wait(() => session.IsVisible, $"Stress capture {i} did not become visible");
                if (i == 0)
                {
                    await Task.Delay(5200);
                    Require(session.IsVisible, "A ready real capture stopped after the original five-second timeout");
                }
                var renderer = session.RendererForDiagnostics!;
                if (adapter is null)
                {
                    using var dxgi = renderer.Device.QueryInterface<IDXGIDevice>();
                    using var nativeAdapter = dxgi.GetAdapter(); adapter = nativeAdapter.QueryInterface<IDXGIAdapter3>();
                }
                await Wait(() => renderer.HasQualityIntermediate && renderer.LastDrawPassCount == 2, "Quality intermediate was not allocated");
                session.SetImageMode(LensImageMode.Clear);
                await Wait(() => !renderer.HasQualityIntermediate && renderer.LastDrawPassCount == 1, "Leaving quality retained its intermediate");
                session.SetImageMode(LensImageMode.HighQuality);
                await Wait(() => renderer.HasQualityIntermediate && renderer.LastDrawPassCount == 2, "Re-entering quality did not recreate its intermediate");
                var handle = session.LensHandle;
                var pointer = session.PointerForDiagnostics!;
                session.Stop(); Released(); Require(!ReviewIsWindow(handle), "Stop retained its native HWND");
                Require(renderer.Device.NativePointer == IntPtr.Zero && !renderer.HasQualityIntermediate,
                    "Stop retained its D3D device or quality intermediate wrapper");
                Require((IntPtr)typeof(LensPointerController).GetField("_hook", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pointer)! == IntPtr.Zero,
                    "Stop retained its native mouse hook handle");
                await Task.Delay(80);
                if (i == 7 || i >= 8 && (i - 7) % 12 == 0) Snapshot(Math.Max(0, i - 7));
            }
            for (var i = 0; i < 24; i++)
            {
                session.Start(monitors, monitor, 640, 384, 4, 1, 0);
                session.Stop(); Released();
            }
            await Task.Delay(300); Released(); Snapshot(60);
            // Native/driver teardown and finalizer work can complete after Stop returns.
            await Task.Delay(2000); Released(); Snapshot(60);
            await Task.Delay(5000); Released(); Snapshot(60);
            await File.WriteAllTextAsync(Path.GetFullPath(output), JsonSerializer.Serialize(new
            {
                StartedAt = DateTimeOffset.Now - watch.Elapsed, DurationSeconds = watch.Elapsed.TotalSeconds,
                WarmupCycles = 8, VisibleMeasuredCycles = 36, ImmediateStopCycles = 24,
                QualitySwitches = 88, NativePointerHooksEnabled = true,
                ReadySessionSurvivedOriginalTimeout = true,
                Driver = adapter!.Description.Description,
                Monitors = monitors.Select(m => new { m.Name, m.Bounds }),
                CounterScope = "Whole test process and selected DXGI adapter; generated scene remains alive across samples. Final snapshot also includes immediate-stop cycles.",
                HoursLongSoakExecuted = false, DeviceLossExecuted = false, Snapshots = snapshots
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS Real capture 44 visible starts, 24 immediate stops, quality intermediate switching and native window cleanup; resource counters recorded");
        }
        finally { session.Stop(); adapter?.Dispose(); }
    }
    [DllImport("user32.dll", EntryPoint = "GetGuiResources")] private static extern uint ReviewGuiResources(IntPtr process, uint flags);
    [DllImport("user32.dll", EntryPoint = "IsWindow")] private static extern bool ReviewIsWindow(IntPtr window);
}
