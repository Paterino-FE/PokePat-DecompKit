using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TilesetEditor.App.Shell;

/// <summary>
/// The taskbar asks the window for its icon the moment the window appears – while PokéPat is still busy loading the
/// project, so the answer comes too late and the taskbar keeps a generic icon (a later WM_SETICON does not change it).
/// Once the app is idle, the taskbar button is removed and added again: the new button asks again and gets the Pokéball.
/// </summary>
public static class TaskbarIcon
{
    [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarList;

    public static void RefreshWhenIdle(Window window)
    {
        // A short delay after the first idle moment: the modules fill their lists in further dispatcher rounds.
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Refresh(window);
        };
        timer.Start();
    }

    public static void Refresh(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !window.ShowInTaskbar || !window.IsVisible) return;
        try
        {
            var list = (ITaskbarList)new TaskbarList();
            list.HrInit();
            list.DeleteTab(hwnd);
            list.AddTab(hwnd);
            if (window.IsActive) list.ActivateTab(hwnd);
            Marshal.ReleaseComObject(list);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException) { }
    }
}
