using System;
using System.Runtime.InteropServices;
using System.Drawing.Printing;
using System.IO;

public class RawPrinterHelper
{
    [DllImport("winspool.Drv", EntryPoint="OpenPrinterA", SetLastError=true)]
    static extern bool OpenPrinter(string szPrinter, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.Drv", EntryPoint="ClosePrinter")]
    static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint="StartDocPrinterA")]
    static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] ref DOCINFOA di);

    [DllImport("winspool.Drv", EntryPoint="EndDocPrinter")]
    static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint="StartPagePrinter")]
    static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint="EndPagePrinter")]
    static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint="WritePrinter")]
    static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    [StructLayout(LayoutKind.Sequential)]
    public struct DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPStr)] public string pOutputFile;
        [MarshalAs(UnmanagedType.LPStr)] public string pDataType;
    }

    public static bool SendBytesToPrinter(string printerName, byte[] bytes)
    {
        IntPtr pPrinter;
        var di = new DOCINFOA() { pDocName = "Cut", pDataType = "RAW" };
        if (!OpenPrinter(printerName, out pPrinter, IntPtr.Zero)) return false;
        if (!StartDocPrinter(pPrinter, 1, ref di)) return false;
        if (!StartPagePrinter(pPrinter)) return false;

        IntPtr pUnmanagedBytes = Marshal.AllocCoTaskMem(bytes.Length);
        Marshal.Copy(bytes, 0, pUnmanagedBytes, bytes.Length);
        bool success = WritePrinter(pPrinter, pUnmanagedBytes, bytes.Length, out _);
        EndPagePrinter(pPrinter);
        EndDocPrinter(pPrinter);
        ClosePrinter(pPrinter);
        Marshal.FreeCoTaskMem(pUnmanagedBytes);
        return success;
    }
}
