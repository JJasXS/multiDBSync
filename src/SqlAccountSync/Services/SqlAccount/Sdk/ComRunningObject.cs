using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SqlAccountSync.Services.SqlAccount.Sdk;

/// <summary>
/// .NET does not ship <c>Marshal.GetActiveObject</c>; P/Invoke OLE to attach to a running LocalServer.
/// </summary>
internal static class ComRunningObject
{
    public const string SqlAccProcessName = "SQLACC";

    public static int CountSqlAccProcesses()
    {
        try
        {
            return Process.GetProcessesByName(SqlAccProcessName).Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Attach to the running SQLAcc.BizApp. Retries because LocalServers often register in the ROT
    /// only after the UI loses focus.
    /// </summary>
    public static bool TryGetActiveObject(string progId, out object? instance, int attempts = 8, int delayMs = 250)
    {
        instance = null;
        if (string.IsNullOrWhiteSpace(progId))
            return false;

        var id = progId.Trim();
        var hrClsid = CLSIDFromProgID(id, out var clsid);
        if (hrClsid < 0)
            return false;

        for (var i = 0; i < Math.Max(1, attempts); i++)
        {
            var hr = GetActiveObject(ref clsid, IntPtr.Zero, out var obj);
            if (hr >= 0 && obj is not null)
            {
                instance = obj;
                return true;
            }

            if (i + 1 < attempts)
                Thread.Sleep(Math.Max(50, delayMs));
        }

        return false;
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
