using ClashTray.App;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class FlyoutPlacementTests
{
    [TestMethod]
    public void TrayVersion4DecodesPackedIconIdAndAvoidsDoubleClick()
    {
        Assert.AreEqual(TrayInteraction.LeftClick, TrayCallback.Decode(0x00010400, true));
        Assert.AreEqual(TrayInteraction.LeftClick, TrayCallback.Decode(0x00010401, true));
        Assert.AreEqual(TrayInteraction.RightClick, TrayCallback.Decode(0x0001007B, true));
        Assert.IsNull(TrayCallback.Decode(0x00010202, true));
        Assert.AreEqual(TrayInteraction.LeftClick, TrayCallback.Decode(0x0202, false));
        Assert.AreEqual(TrayInteraction.RightClick, TrayCallback.Decode(0x0205, false));
    }

    [TestMethod]
    public void MonitorInfoMatchesNativeAbiAndQueriesRealMonitor()
    {
        Assert.AreEqual(40, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>());
        var rect = new NativeMethods.Rect { Right = 1, Bottom = 1 };
        var monitor = NativeMethods.MonitorFromRect(ref rect, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MonitorInfo { Size = 40 };
        Assert.IsTrue(NativeMethods.GetMonitorInfo(monitor, ref info));
        Assert.IsTrue(info.Work.Width > 0 && info.Work.Height > 0);
    }

    [TestMethod]
    public void BottomTaskbarDocksToWorkAreaCorner()
    {
        var result = FlyoutPlacement.Calculate(new(0,0,1920,1080), new(0,0,1920,1032), 1);
        Assert.AreEqual(new ScreenBounds(1492,344,420,680), result);
    }

    [TestMethod]
    public void NegativeOriginMonitorAndHighDpiAreSupported()
    {
        var result = FlyoutPlacement.Calculate(new(-2560,0,2560,1440), new(-2560,0,2560,1380), 1.5);
        Assert.AreEqual(-12, result.Right);
        Assert.AreEqual(1368, result.Bottom);
        Assert.AreEqual(630, result.Width);
    }

    [TestMethod]
    public void TinyWorkAreaClampsSizeInsteadOfThrowing()
    {
        var result = FlyoutPlacement.Calculate(new(0,0,320,240), new(0,0,320,200), 2);
        Assert.IsTrue(result.Width > 0 && result.Height > 0);
        Assert.IsTrue(result.Right <= 320 && result.Bottom <= 200);
        Assert.IsTrue(result.X >= 0 && result.Y >= 0);
    }

    [TestMethod]
    public void TopAndLeftTaskbarsKeepPanelOnWorkAreaSide()
    {
        var top = FlyoutPlacement.Calculate(new(0,0,1920,1080), new(0,48,1920,1032), 1);
        Assert.AreEqual(56, top.Y);
        var left = FlyoutPlacement.Calculate(new(0,0,1920,1080), new(48,0,1872,1080), 1);
        Assert.AreEqual(56, left.X);
        var right = FlyoutPlacement.Calculate(new(0,0,1920,1080), new(0,0,1872,1080), 1);
        Assert.AreEqual(1864, right.Right);
    }
}
