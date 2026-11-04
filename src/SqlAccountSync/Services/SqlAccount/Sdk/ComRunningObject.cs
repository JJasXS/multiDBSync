using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace SqlAccountSync.Services.SqlAccount.Sdk;

/// <summary>
/// Attach helpers for SQLAcc.BizApp. Interactive SQL Account often does <b>not</b> call
/// RegisterActiveObject, so GetActiveObject alone usually fails; ROT scan is a second try.
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

    public static bool TryAttach(string progId, out object? instance, int attempts = 6, int delayMs = 200)
    {
        instance = null;
        if (string.IsNullOrWhiteSpace(progId))
            return false;

        for (var i = 0; i < Math.Max(1, attempts); i++)
        {
            if (TryGetActiveObject(progId, out instance) && instance is not null)
                return true;
            if (TryGetFromRunningObjectTable(progId, out instance) && instance is not null)
                return true;

            if (i + 1 < attempts)
                Thread.Sleep(Math.Max(50, delayMs));
        }

        return false;
    }

    public static bool TryGetActiveObject(string progId, out object? instance)
    {
        instance = null;
        var hr = CLSIDFromProgID(progId.Trim(), out var clsid);
        if (hr < 0)
            return false;

        hr = GetActiveObject(ref clsid, IntPtr.Zero, out var obj);
        if (hr < 0 || obj is null)
            return false;

        instance = obj;
        return true;
    }

    /// <summary>Scan the Running Object Table for a moniker that looks like SQLAcc.BizApp.</summary>
    public static bool TryGetFromRunningObjectTable(string progId, out object? instance)
    {
        instance = null;
        IRunningObjectTable? rot = null;
        IEnumMoniker? enumerator = null;
        try
        {
            if (GetRunningObjectTable(0, out rot) != 0 || rot is null)
                return false;

            rot.EnumRunning(out enumerator);
            if (enumerator is null)
                return false;

            var needle = (progId ?? "").Trim();
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                if (moniker is null)
                    continue;

                try
                {
                    var name = GetDisplayName(moniker);
                    if (!NameLooksLikeSqlAcc(name, needle))
                        continue;

                    rot.GetObject(moniker, out var obj);
                    if (obj is not null)
                    {
                        instance = obj;
                        return true;
                    }
                }
                finally
                {
                    try { Marshal.ReleaseComObject(moniker); } catch { /* ignore */ }
                }
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (enumerator is not null)
                try { Marshal.ReleaseComObject(enumerator); } catch { /* ignore */ }
            if (rot is not null)
                try { Marshal.ReleaseComObject(rot); } catch { /* ignore */ }
        }

        return false;
    }

    private static bool NameLooksLikeSqlAcc(string displayName, string progId)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return false;
        if (displayName.Contains("SQLAcc", StringComparison.OrdinalIgnoreCase))
            return true;
        if (displayName.Contains("SQLACC", StringComparison.OrdinalIgnoreCase))
            return true;
        if (progId.Length > 0 && displayName.Contains(progId, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static string GetDisplayName(IMoniker moniker)
    {
        IBindCtx? ctx = null;
        try
        {
            CreateBindCtx(0, out ctx);
            moniker.GetDisplayName(ctx, null, out var name);
            return name ?? "";
        }
        catch
        {
            return "";
        }
        finally
        {
            if (ctx is not null)
                try { Marshal.ReleaseComObject(ctx); } catch { /* ignore */ }
        }
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

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable pprot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);
}
