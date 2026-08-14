using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgenTally.Tests.UI;

internal sealed class IsolatedDesktop
{
    private const uint GenericAll = 0x10000000;
    private const uint DesktopReadObjects = 0x0001;
    private readonly SafeDesktopHandle _handle;

    private IsolatedDesktop(string name, SafeDesktopHandle handle)
    {
        Name = name;
        _handle = handle;
    }

    public string Name { get; }

    public static IsolatedDesktop CreateForCurrentThread()
    {
        string name = $"AgenTally.Tests.{Environment.ProcessId}.{Guid.NewGuid():N}";
        SafeDesktopHandle handle = CreateDesktopW(
            name,
            IntPtr.Zero,
            IntPtr.Zero,
            0,
            GenericAll,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            Win32Exception exception = NewWin32Exception(
                "无法为 WindowedDesktop 测试创建隔离桌面。");
            handle.Dispose();
            throw exception;
        }

        return new IsolatedDesktop(name, handle);
    }

    public static bool CanOpen(string name)
    {
        using SafeDesktopHandle handle = OpenDesktopW(
            name,
            0,
            inherit: false,
            DesktopReadObjects);
        return !handle.IsInvalid;
    }

    public void CloseChecked() => _handle.CloseChecked();

    private static Win32Exception NewWin32Exception(string message)
    {
        int error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{message} Win32 error: {error}.");
    }

    [DllImport(
        "user32.dll",
        EntryPoint = "CreateDesktopW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeDesktopHandle CreateDesktopW(
        string desktopName,
        IntPtr device,
        IntPtr devMode,
        uint flags,
        uint desiredAccess,
        IntPtr securityAttributes);

    [DllImport(
        "user32.dll",
        EntryPoint = "OpenDesktopW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeDesktopHandle OpenDesktopW(
        string desktopName,
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    private sealed class SafeDesktopHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeDesktopHandle()
            : base(ownsHandle: true)
        {
        }

        public void CloseChecked()
        {
            if (IsClosed || IsInvalid)
            {
                return;
            }

            if (!CloseDesktop(handle))
            {
                throw NewWin32Exception(
                    "无法关闭 WindowedDesktop 测试的隔离桌面句柄。");
            }

            SetHandleAsInvalid();
            Dispose();
        }

        protected override bool ReleaseHandle() => CloseDesktop(handle);
    }
}
