using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class WindowsIpv6ListenerRowTests
{
    [TestMethod]
    [DataRow(0, 0u)]
    [DataRow(0, 7u)]
    [DataRow(0, 8u)]
    [DataRow(16, 7u)]
    [DataRow(0, uint.MaxValue)]
    public void NativeIpv6AddressPreservesTheUnsignedInterfaceScope(int offset, uint scope)
    {
        byte[] address = IPAddress.Parse("fe80::1").GetAddressBytes();
        IntPtr row = Marshal.AllocHGlobal(offset + address.Length + sizeof(uint));
        try
        {
            Marshal.Copy(address, 0, IntPtr.Add(row, offset), address.Length);
            Marshal.WriteInt32(row, offset + address.Length, unchecked((int)scope));
            MethodInfo parser = typeof(WindowsListenerOwnerTable).GetMethod("ReadIpv6Address", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The native listener address parser was not found.");
            IPAddress actual = (IPAddress)(parser.Invoke(null, [row, offset])
                ?? throw new InvalidOperationException("The native listener address parser returned no address."));

            Assert.AreEqual((long)scope, actual.ScopeId, "Dropping the interface scope changes exact listener ownership.");
            Assert.AreEqual(new IPAddress(address, scope), actual);
            if (scope != 0)
            {
                Assert.IsFalse(WindowsListenerOwnerTable.AddressMatches(new IPAddress(address, 0), actual),
                    "A scoped listener must not prove ownership of an address on another interface.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(row);
        }
    }
}
