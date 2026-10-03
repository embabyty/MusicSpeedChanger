using System;
using System.Runtime.InteropServices;

namespace MusicSpeedChanger.Services;

/// <summary>
/// Mirrors song playback onto the Windows taskbar button, Cider-style:
/// green fill while playing, yellow when paused, hidden when stopped,
/// plus Prev / Play-Pause / Next buttons in the taskbar thumbnail preview.
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public uint dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;
        public uint dwFlags;
    }

    private const uint THB_ICON = 0x2;
    private const uint THB_TOOLTIP = 0x4;
    private const uint THB_FLAGS = 0x8;
    private const uint THBF_ENABLED = 0x0;
    private const uint THBF_DISABLED = 0x1;

    private const uint PrevButtonId = 1001;
    private const uint PlayPauseButtonId = 1002;
    private const uint NextButtonId = 1003;

    private const int WM_COMMAND = 0x0111;
    private const int THBN_CLICKED = 0x1800;

    // Vtable order must match ITaskbarList -> ITaskbarList2 -> ITaskbarList3.
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
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] pButton);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] pButton);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string? pszDescription);
        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? pszTip);
        void SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, uint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, uint dwRefData);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    // GDI icon rendering (Segoe MDL2 glyphs -> HICON for thumb buttons).
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, IntPtr lpvBits);
    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int iBkMode);
    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint crColor);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint dwDTFormat);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int nHeight, int nWidth, int nEscapement, int nOrientation, int fnWeight,
        uint fdwItalic, uint fdwUnderline, uint fdwStrikeOut, uint fdwCharSet, uint fdwOutputPrecision,
        uint fdwClipPrecision, uint fdwQuality, uint fdwPitchAndFamily, string lpszFace);
    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public uint[]? bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    public event EventHandler? PrevClicked;
    public event EventHandler? PlayPauseClicked;
    public event EventHandler? NextClicked;

    private ITaskbarList3? _taskbar;
    private bool _initialized;
    private bool _disposed;

    // Last pushed state — skips redundant Explorer IPC on the 100 ms tick.
    private int _lastCompleted = -1;
    private TBPFLAG _lastState = (TBPFLAG)uint.MaxValue;

    // Thumbnail toolbar state.
    private IntPtr _thumbHwnd = IntPtr.Zero;
    private bool _thumbReady;
    private SubclassProc? _subclassProc;
    private IntPtr _prevIcon = IntPtr.Zero;
    private IntPtr _playIcon = IntPtr.Zero;
    private IntPtr _pauseIcon = IntPtr.Zero;
    private IntPtr _nextIcon = IntPtr.Zero;
    private bool _lastThumbPlaying;
    private bool _lastThumbHasTrack;
    private bool _lastThumbCanPrev;
    private bool _lastThumbCanNext;
    private bool _lastThumbPushed;

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

    /// <summary>
    /// Adds Prev / Play-Pause / Next buttons to the taskbar thumbnail preview.
    /// Call once after the window exists. Safe to call repeatedly.
    /// </summary>
    public void EnsureThumbBar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _disposed || _thumbReady) return;
        try
        {
            if (!EnsureInitialized() || _taskbar == null) return;

            _prevIcon = CreateGlyphIcon("\uE100") /* Previous */;
            _playIcon = CreateGlyphIcon("\uE768") /* Play */;
            _pauseIcon = CreateGlyphIcon("\uE769") /* Pause */;
            _nextIcon = CreateGlyphIcon("\uE101") /* Next */;

            _subclassProc = SubclassHandler;
            try { SetWindowSubclass(hwnd, _subclassProc, IntPtr.Zero, 0); }
            catch { /* subclass is best-effort; buttons still show */ }

            _thumbHwnd = hwnd;
            var buttons = BuildButtons(isPlaying: false, hasTrack: false, canPrev: false, canNext: false);
            _taskbar.ThumbBarAddButtons(hwnd, (uint)buttons.Length, buttons);
            _thumbReady = true;
            _lastThumbPushed = false;
        }
        catch { /* thumbnail toolbar is best-effort */ }
    }

    /// <summary>
    /// Refreshes thumb-button icons (play vs pause) and enabled states.
    /// Skips the Explorer IPC when nothing changed.
    /// </summary>
    public void UpdateThumbButtons(IntPtr hwnd, bool isPlaying, bool hasTrack, bool canPrev, bool canNext)
    {
        if (hwnd == IntPtr.Zero || _disposed) return;
        try
        {
            if (!_thumbReady)
            {
                EnsureThumbBar(hwnd);
                if (!_thumbReady) return;
            }
            if (_taskbar == null) return;
            if (_lastThumbPushed && isPlaying == _lastThumbPlaying && hasTrack == _lastThumbHasTrack &&
                canPrev == _lastThumbCanPrev && canNext == _lastThumbCanNext) return;

            var buttons = BuildButtons(isPlaying, hasTrack, canPrev, canNext);
            _taskbar.ThumbBarUpdateButtons(hwnd, (uint)buttons.Length, buttons);
            _lastThumbPlaying = isPlaying;
            _lastThumbHasTrack = hasTrack;
            _lastThumbCanPrev = canPrev;
            _lastThumbCanNext = canNext;
            _lastThumbPushed = true;
        }
        catch { /* ignore */ }
    }

    private THUMBBUTTON[] BuildButtons(bool isPlaying, bool hasTrack, bool canPrev, bool canNext)
    {
        uint playPauseIconFlags = THB_ICON | THB_TOOLTIP | THB_FLAGS;
        return new[]
        {
            new THUMBBUTTON
            {
                dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS,
                iId = PrevButtonId,
                hIcon = _prevIcon,
                szTip = "Previous",
                dwFlags = canPrev ? THBF_ENABLED : THBF_DISABLED,
            },
            new THUMBBUTTON
            {
                dwMask = playPauseIconFlags,
                iId = PlayPauseButtonId,
                hIcon = isPlaying ? _pauseIcon : _playIcon,
                szTip = isPlaying ? "Pause" : "Play",
                dwFlags = hasTrack ? THBF_ENABLED : THBF_DISABLED,
            },
            new THUMBBUTTON
            {
                dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS,
                iId = NextButtonId,
                hIcon = _nextIcon,
                szTip = "Next",
                dwFlags = canNext ? THBF_ENABLED : THBF_DISABLED,
            },
        };
    }

    private IntPtr SubclassHandler(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, uint dwRefData)
    {
        try
        {
            if (uMsg == WM_COMMAND)
            {
                long v = wParam.ToInt64();
                int hi = (int)((v >> 16) & 0xFFFF);
                int lo = (int)(v & 0xFFFF);
                if (hi == THBN_CLICKED)
                {
                    if (lo == (int)PrevButtonId) PrevClicked?.Invoke(this, EventArgs.Empty);
                    else if (lo == (int)PlayPauseButtonId) PlayPauseClicked?.Invoke(this, EventArgs.Empty);
                    else if (lo == (int)NextButtonId) NextClicked?.Invoke(this, EventArgs.Empty);
                    return IntPtr.Zero;
                }
            }
        }
        catch { /* never break the message pump */ }
        try { return DefSubclassProc(hWnd, uMsg, wParam, lParam); }
        catch { return IntPtr.Zero; }
    }

    /// <summary>Renders a Segoe MDL2 Assets glyph into a 32x32 HICON. Never throws.</summary>
    private static IntPtr CreateGlyphIcon(string glyph)
    {
        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdc = IntPtr.Zero;
        IntPtr hFont = IntPtr.Zero;
        IntPtr hBmpColor = IntPtr.Zero;
        IntPtr hBmpMask = IntPtr.Zero;
        IntPtr hOldBmp = IntPtr.Zero;
        IntPtr hOldFont = IntPtr.Zero;
        try
        {
            const int size = 32;
            hdcScreen = GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero) return LoadIconW(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
            hdc = CreateCompatibleDC(hdcScreen);
            if (hdc == IntPtr.Zero) return LoadIconW(IntPtr.Zero, new IntPtr(32512));

            var bmi = new BITMAPINFO
            {
                biSize = 40,
                biWidth = size,
                biHeight = size,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            hBmpColor = CreateDIBSection(hdc, ref bmi, 0, out _, IntPtr.Zero, 0);
            if (hBmpColor == IntPtr.Zero) return LoadIconW(IntPtr.Zero, new IntPtr(32512));
            hBmpMask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
            if (hBmpMask == IntPtr.Zero) return LoadIconW(IntPtr.Zero, new IntPtr(32512));

            hOldBmp = SelectObject(hdc, hBmpColor);
            hFont = CreateFontW(24, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe MDL2 Assets");
            if (hFont != IntPtr.Zero) hOldFont = SelectObject(hdc, hFont);
            SetBkMode(hdc, 1 /* TRANSPARENT */);
            SetTextColor(hdc, 0x00FFFFFF);
            var rc = new RECT { left = 0, top = 0, right = size, bottom = size };
            DrawTextW(hdc, glyph, glyph.Length, ref rc, 0x0001 /* DT_CENTER */ | 0x0004 /* DT_VCENTER */ | 0x0020 /* DT_SINGLELINE */);
            if (hOldFont != IntPtr.Zero) SelectObject(hdc, hOldFont);
            if (hOldBmp != IntPtr.Zero) SelectObject(hdc, hOldBmp);

            var info = new ICONINFO { fIcon = true, hbmMask = hBmpMask, hbmColor = hBmpColor };
            IntPtr hIcon = CreateIconIndirect(ref info);
            if (hIcon == IntPtr.Zero) return LoadIconW(IntPtr.Zero, new IntPtr(32512));
            return hIcon;
        }
        catch { try { return LoadIconW(IntPtr.Zero, new IntPtr(32512)); } catch { return IntPtr.Zero; } }
        finally
        {
            if (hBmpColor != IntPtr.Zero) DeleteObject(hBmpColor);
            if (hBmpMask != IntPtr.Zero) DeleteObject(hBmpMask);
            if (hFont != IntPtr.Zero) DeleteObject(hFont);
            if (hdc != IntPtr.Zero) DeleteDC(hdc);
            if (hdcScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_thumbReady && _thumbHwnd != IntPtr.Zero && _subclassProc != null)
                try { RemoveWindowSubclass(_thumbHwnd, _subclassProc, IntPtr.Zero); } catch { /* ignore */ }
        }
        catch { /* ignore */ }
        _thumbReady = false;
        _thumbHwnd = IntPtr.Zero;
        foreach (var h in new[] { _prevIcon, _playIcon, _pauseIcon, _nextIcon })
        {
            try { if (h != IntPtr.Zero) DestroyIcon(h); } catch { /* ignore */ }
        }
        _prevIcon = _playIcon = _pauseIcon = _nextIcon = IntPtr.Zero;
        try
        {
            if (_taskbar != null)
                Marshal.ReleaseComObject(_taskbar);
        }
        catch { /* ignore */ }
        _taskbar = null;
    }
}
