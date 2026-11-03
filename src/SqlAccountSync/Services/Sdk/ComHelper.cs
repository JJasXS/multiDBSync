using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace SqlAccountSync.Services.Sdk;

/// <summary>COM registration, bitness, release helpers for SQLAcc.BizApp.</summary>
public static class ComHelper
{
    public const string ProgId = "SQLAcc.BizApp";

    public sealed class RegistrationInfo
    {
        public bool ProgIdFound { get; set; }
        public string Clsid { get; set; } = "";
        public string LocalServer32 { get; set; } = "";
        public string ServerArchitecture { get; set; } = "unknown";
        public string Guidance { get; set; } = "";
    }

    public static RegistrationInfo ProbeRegistration()
    {
        var info = new RegistrationInfo();
        try
        {
            using var prog = Registry.ClassesRoot.OpenSubKey(ProgId + @"\CLSID");
            if (prog is null)
            {
                info.Guidance =
                    "SQLAcc.BizApp ProgID is not registered. Install SQL Account " +
                    "or run SQLACC.exe /regserver from an elevated prompt.";
                return info;
            }

            info.ProgIdFound = true;
            info.Clsid = (prog.GetValue("") as string) ?? "";
            if (info.Clsid.Length == 0)
            {
                info.Guidance = "ProgID found but CLSID is empty.";
                return info;
            }

            foreach (var root in new[]
                     {
                         @"SOFTWARE\Classes\CLSID\" + info.Clsid,
                         @"SOFTWARE\WOW6432Node\Classes\CLSID\" + info.Clsid
                     })
            {
                using var key = Registry.LocalMachine.OpenSubKey(root);
                if (key is null) continue;
                using var ls = key.OpenSubKey("LocalServer32");
                if (ls is null) continue;
                info.LocalServer32 = Unquote((ls.GetValue("") as string) ?? "");
                if (info.LocalServer32.Length > 0)
                    break;
            }

            if (info.LocalServer32.Length == 0)
                info.Guidance = "CLSID registered but LocalServer32 is missing.";
            else if (!File.Exists(info.LocalServer32))
                info.Guidance = "LocalServer32 path is registered but the file was not found: " + info.LocalServer32;
            else
                info.ServerArchitecture = GetPeArchitecture(info.LocalServer32);
        }
        catch (Exception ex)
        {
            info.Guidance = "Failed to probe COM registration: " + FormatException(ex);
        }

        if (info.ProgIdFound && info.Guidance.Length == 0)
        {
            info.Guidance =
                "COM ProgID is registered. Out-of-process LocalServer32; client bitness need not match.";
        }

        return info;
    }

    public static string GetProcessArchitecture() =>
        Environment.Is64BitProcess ? "x64" : "x86";

    public static string GetPeArchitecture(string exePath)
    {
        try
        {
            using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt16() != 0x5A4D) return "unknown";
            fs.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = br.ReadInt32();
            fs.Seek(peOffset, SeekOrigin.Begin);
            if (br.ReadUInt32() != 0x00004550) return "unknown";
            var machine = br.ReadUInt16();
            return machine switch
            {
                0x014C => "x86",
                0x8664 => "x64",
                0xAA64 => "ARM64",
                _ => "0x" + machine.ToString("X4")
            };
        }
        catch
        {
            return "unknown";
        }
    }

    public static void SafeRelease(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
            return;
        try { Marshal.FinalReleaseComObject(comObject); }
        catch { /* ignore */ }
    }

    public static string FormatException(Exception? ex)
    {
        if (ex is null) return "";
        var sb = new StringBuilder();
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (sb.Length > 0) sb.Append(" | ");
            if (cur is COMException com)
                sb.Append("HRESULT=0x").Append(com.ErrorCode.ToString("X8")).Append(' ');
            sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
        }
        return sb.ToString();
    }

    public static string DecodeHResult(int hr) => unchecked((uint)hr) switch
    {
        0x80040154 => "REGDB_E_CLASSNOTREG — COM class not registered.",
        0x800401F3 => "CO_E_CLASSSTRING — Invalid ProgID.",
        0x80070005 => "E_ACCESSDENIED — Access denied.",
        0x800706BA => "RPC_S_SERVER_UNAVAILABLE — COM server unavailable.",
        0x80080005 => "CO_E_SERVER_EXEC_FAILURE — Failed to start LocalServer32.",
        _ => "HRESULT 0x" + hr.ToString("X8")
    };

    public static readonly string[] UnverifiedDetailDataSetNames =
    [
        "cdsDocDetail",
        "cdsDocumentDetail",
        "MainDetailDataSet",
        "DetailDataSet",
        "sdsdocdetail"
    ];

    /// <summary>Best-effort field enumeration via Fields.Count / Items(i).FieldName.</summary>
    public static string[] TryEnumerateFields(dynamic dataSet)
    {
        try
        {
            var fields = dataSet.Fields;
            int count = (int)fields.Count;
            var names = new string[count];
            for (var i = 0; i < count; i++)
            {
                dynamic f = fields.Items(i);
                names[i] = (string)f.FieldName;
                SafeRelease(f);
            }
            SafeRelease(fields);
            return names;
        }
        catch
        {
            return [];
        }
    }

    private static string Unquote(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        path = path.Trim();
        if (path.StartsWith('"') && path.EndsWith('"') && path.Length >= 2)
            path = path[1..^1];
        var exe = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0 && exe + 4 < path.Length)
            path = path[..(exe + 4)];
        return path.Trim();
    }
}
