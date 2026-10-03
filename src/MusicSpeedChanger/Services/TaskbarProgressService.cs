using System;
using System.Runtime.InteropServices;

namespace MusicSpeedChanger.Services;

/// <summary>
/// Mirrors song playback onto the Windows taskbar button, Cider-style:
/// green fill while playing, yellow when paused, hidden when stopped.
/// Uses ITaskbarList3 directly so it works in unpackaged WinUI 3 apps.
/// All methods are best-effort and never throw.
/// </summary>
public sealed class TaskbarProgressService : IDisposable
{
    private enum TBPFLAG : uint
    {
        NOPROGRESS = 0,
        INDETERMINATE = 0x1,
        NORMAL = 0x2,
        ERROR = 0x4,
        PAUSED = 0x8,
    }

    // NOTE: intentionally not sealed — C# only allows a class-to-interface
    // explicit conversion when the class is unsealed (COM QI happens at runtime).
    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList
    {
    }

    // Vtable order must match ITaskbarList -> ITaskbarList2 -> ITaskbarList3
    // up to SetProgressState; later methods are not needed.
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, TBPFLAG tbpFlags);
    }

    private ITaskbarList3? _taskbar;
    private bool _initialized;
    private bool _disposed;

    // Last pushed state — skips redundant Explorer IPC on the 100 ms tick.
    private int _lastCompleted = -1;
    private TBPFLAG _lastState = (TBPFLAG)uint.MaxValue;

    private bool EnsureInitialized()
    {
        if (_disposed) return false;
        if (_initialized) return _taskbar != null;
        try
        {
            _taskbar = (ITaskbarList3)new TaskbarList();
            _taskbar.HrInit();
            _initialized = true;
            return true;
        }
        catch
        {
            _taskbar = null;
            _initialized = false;
            return false;
        }
    }

    /// <summary>Show playback progress. Paused renders yellow, playing renders green.</summary>
    public void SetProgress(IntPtr hwnd, double progress, bool paused)
    {
        if (hwnd == IntPtr.Zero || _disposed) return;
        try
        {
            if (!EnsureInitialized() || _taskbar == null) return;
            progress = Math.Clamp(progress, 0, 1);
            const ulong total = 1000;
            var completed = (ulong)Math.Round(progress * total);
            var state = paused ? TBPFLAG.PAUSED : TBPFLAG.NORMAL;

            if (completed == (ulong)_lastCompleted && state == _lastState) return;

            _taskbar.SetProgressState(hwnd, state);
            _taskbar.SetProgressValue(hwnd, completed, total);
            _lastCompleted = (int)completed;
            _lastState = state;
        }
        catch { /* taskbar is best-effort */ }
    }

    /// <summary>Hide the taskbar progress (stopped / nothing loaded).</summary>
    public void Clear(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _disposed) return;
        try
        {
            if (!EnsureInitialized() || _taskbar == null) return;
            if (_lastState == TBPFLAG.NOPROGRESS) return;
            _taskbar.SetProgressState(hwnd, TBPFLAG.NOPROGRESS);
            _lastCompleted = -1;
            _lastState = TBPFLAG.NOPROGRESS;
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_taskbar != null)
                Marshal.ReleaseComObject(_taskbar);
        }
        catch { /* ignore */ }
        _taskbar = null;
    }
}
