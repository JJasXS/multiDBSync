using System.Runtime.InteropServices;

namespace SqlAccountSync.Services.SqlAccount.Sdk;

/// <summary>
/// .NET does not ship <c>Marshal.GetActiveObject</c>; P/Invoke OLE to attach to a running LocalServer.
/// </summary>
internal static class ComRunningObject
{
    public static bool TryGetActiveObject(string progId, out object? instance)
    {
        instance = null;
        if (string.IsNullOrWhiteSpace(progId))
            return false;

        var hr = CLSIDFromProgID(progId.Trim(), out var clsid);
        if (hr < 0)
            return false;

        hr = GetActiveObject(ref clsid, IntPtr.Zero, out var obj);
        if (hr < 0 || obj is null)
            return false;

        instance = obj;
        return true;
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = true)]
    private static extern int CLSIDFromProgID(
        [MarshalAs(UnmanagedType.LPWStr)] string lpszProgID,
        out Guid pclsid);

    [DllImport("oleaut32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int GetActiveObject(
        ref Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);
}
