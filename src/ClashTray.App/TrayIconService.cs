using ClashTray.Contracts;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace ClashTray.App;

internal sealed class TrayIconService : IDisposable
{
    private const uint CallbackMessage = NativeMethods.WM_USER + 0x2A1;
    private readonly IntPtr _windowHandle;
    private readonly NativeMethods.WindowProc _windowProc;
    private readonly Action<TrayInteraction> _interactionHandler;
    private readonly Action _deactivationHandler;
    private readonly uint _taskbarCreatedMessage;
    private IntPtr _previousWindowProc;
    private NativeMethods.NotifyIconData _data;
    private bool _installed;
    private bool _disposed;
    private bool _ownsIcon;
    private bool _version4;
    private static readonly Guid IconIdentity = new("06eaac46-982f-4741-b490-6f8f2fe8bb21");

    public TrayIconService(
        Microsoft.UI.Xaml.Window window,
        Action<TrayInteraction> interactionHandler,
        Action deactivationHandler)
    {
        _windowHandle = WindowNative.GetWindowHandle(window);
        _interactionHandler = interactionHandler;
        _deactivationHandler = deactivationHandler;
        _windowProc = WindowProc;
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    }

    public TrayState State { get; private set; } = TrayState.Stopped;

    public void Install()
    {
        if (_installed)
        {
            return;
        }

        var extendedStyle = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GWL_EXSTYLE).ToInt64();
        extendedStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        extendedStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWL_EXSTYLE, new IntPtr(extendedStyle));
        if (_previousWindowProc == IntPtr.Zero)
        {
            _previousWindowProc = NativeMethods.SetWindowLongPtr(
                _windowHandle,
                -4,
                Marshal.GetFunctionPointerForDelegate(_windowProc));
        }

        _data = new NativeMethods.NotifyIconData
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NotifyIconData>(),
            WindowHandle = _windowHandle,
            Id = 1,
            Flags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_GUID | NativeMethods.NIF_SHOWTIP,
            GuidItem = IconIdentity,
            CallbackMessage = CallbackMessage,
            IconHandle = LoadTrayIcon(),
            Tip = string.Empty,
            Info = string.Empty,
            InfoTitle = string.Empty
        };
        _installed = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _data);
        if (_installed)
        {
            _data.TimeoutOrVersion = NativeMethods.NOTIFYICON_VERSION_4;
            _version4 = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref _data);
            SetState(TrayState.Stopped);
        }
    }

    public void SetState(TrayState state)
    {
        var changed = State != state;
        State = state;
        if (!_installed)
        {
            return;
        }

        if (changed)
        {
            ReleaseIcon();
            _data.IconHandle = LoadTrayIcon();
        }

        _data.Tip = state switch
        {
            TrayState.Running => "ClashTray · 运行中",
            TrayState.SystemProxy => "ClashTray · 系统代理已开启",
            TrayState.Tun => "ClashTray · TUN 已开启",
            TrayState.Error => "ClashTray · 需要注意",
            _ => "ClashTray · 已停止"
        };
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref _data);
    }

    public bool TryGetIconRect(out NativeMethods.Rect rect)
    {
        var identifier = new NativeMethods.NotifyIconIdentifier
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NotifyIconIdentifier>(),
            WindowHandle = _windowHandle,
            Id = 1,
            Guid = IconIdentity
        };
        return NativeMethods.Shell_NotifyIconGetRect(ref identifier, out rect) == NativeMethods.S_OK;
    }

    public void ShowContextMenu(IReadOnlyList<(uint Id, string Text)> items)
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            foreach (var item in items)
            {
                if (item.Id == 0)
                {
                    NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
                }
                else
                {
                    NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, item.Id, item.Text);
                }
            }

            NativeMethods.GetCursorPos(out var point);
            NativeMethods.SetForegroundWindow(_windowHandle);
            var selected = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD,
                point.X,
                point.Y,
                0,
                _windowHandle,
                IntPtr.Zero);
            NativeMethods.PostMessage(_windowHandle, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (selected != 0)
            {
                MenuItemSelected?.Invoke(selected);
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    public event Action<uint>? MenuItemSelected;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_installed)
        {
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref _data);
            _installed = false;
        }

        if (_previousWindowProc != IntPtr.Zero)
        {
            NativeMethods.SetWindowLongPtr(_windowHandle, -4, _previousWindowProc);
        }

        ReleaseIcon();
    }

    private IntPtr WindowProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            var interaction = TrayCallback.Decode(lParam.ToInt64(), _version4);
            if (interaction is not null) _interactionHandler(interaction.Value);
        }
        else if (message == _taskbarCreatedMessage)
        {
            ReleaseIcon();
            _installed = false;
            Install();
        }
        else if (message == NativeMethods.WM_ACTIVATE && (wParam.ToInt32() & 0xFFFF) == NativeMethods.WA_INACTIVE)
        {
            _deactivationHandler();
        }

        return _previousWindowProc == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.CallWindowProc(_previousWindowProc, windowHandle, message, wParam, lParam);
    }

    private IntPtr LoadTrayIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico");
        var handle = NativeMethods.LoadImage(
            IntPtr.Zero,
            path,
            NativeMethods.IMAGE_ICON,
            0,
            0,
            NativeMethods.LR_LOADFROMFILE | NativeMethods.LR_DEFAULTSIZE);
        if (handle != IntPtr.Zero)
        {
            _ownsIcon = true;
            return handle;
        }

        _ownsIcon = false;
        return NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(NativeMethods.IDI_APPLICATION));
    }

    private void ReleaseIcon()
    {
        if (_ownsIcon && _data.IconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_data.IconHandle);
        }

        _data.IconHandle = IntPtr.Zero;
        _ownsIcon = false;
    }
}
