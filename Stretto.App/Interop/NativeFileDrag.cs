using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Stretto.App.Interop;

/// <summary>Starts the same native CF_HDROP drag used by Explorer and XDM.</summary>
internal static class NativeFileDrag
{
    private const short CfHdrop = 15;
    private const int DragDropSUseDefaultCursors = 0x00040102;
    private const int DragDropSDrop = 0x00040100;
    private const int DragDropSCancel = 0x00040101;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;

    public static bool Start(string path)
    {
        if (!Directory.Exists(path)) return false;
        try
        {
            var result = DoDragDrop(new FileDropDataObject(Path.GetFullPath(path)), new DropSource(), 1, out _);
            return result is 0 or DragDropSDrop or DragDropSCancel;
        }
        catch (Exception)
        {
            // A shell drag is optional UI. A COM failure must never terminate the app.
            return false;
        }
    }

    public static bool IsLeftButtonDown => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop([MarshalAs(UnmanagedType.Interface)] IDataObject dataObject,
        [MarshalAs(UnmanagedType.Interface)] IDropSource dropSource, uint allowedEffects, out uint effect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    [ComVisible(true), Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escapePressed, uint keyState);
        [PreserveSig] int GiveFeedback(uint effect);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DropSource : IDropSource
    {
        public int QueryContinueDrag(bool escapePressed, uint keyState)
        {
            // WinUI can keep reporting the original pointer state after it has
            // released capture. Ask Windows for the physical button state so the
            // OLE loop always terminates when the user lets go.
            if (escapePressed) return DragDropSCancel;
            if ((GetAsyncKeyState(0x01) & 0x8000) == 0) return DragDropSDrop;
            return 0;
        }
        public int GiveFeedback(uint effect) => DragDropSUseDefaultCursors;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class FileDropDataObject(string path) : IDataObject
    {
        private static FORMATETC Format => new() { cfFormat = CfHdrop, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            if (QueryGetData(ref format) != 0) Marshal.ThrowExceptionForHR(unchecked((int)0x80040064));
            var bytes = 20 + ((path.Length + 2) * sizeof(char));
            var handle = GlobalAlloc(GmemMoveable | GmemZeroInit, (nuint)bytes);
            if (handle == IntPtr.Zero) throw new OutOfMemoryException();
            var target = GlobalLock(handle);
            if (target == IntPtr.Zero) throw new OutOfMemoryException();
            try
            {
                Marshal.WriteInt32(target, 0, 20); // DROPFILES.pFiles
                Marshal.WriteInt32(target, 16, 1); // DROPFILES.fWide
                var chars = (path + "\0\0").ToCharArray();
                Marshal.Copy(chars, 0, target + 20, chars.Length);
            }
            finally { GlobalUnlock(handle); }
            medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle };
        }

        public int QueryGetData(ref FORMATETC format) => format.cfFormat == CfHdrop
            && (format.tymed & TYMED.TYMED_HGLOBAL) != 0 && format.dwAspect == DVASPECT.DVASPECT_CONTENT
                ? 0 : unchecked((int)0x80040064);
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => direction == DATADIR.DATADIR_GET
            ? new FormatEnumerator(Format) : throw new COMException("Not implemented", unchecked((int)0x80004001));
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("Not implemented", unchecked((int)0x80004001));
        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) { formatOut = formatIn; formatOut.ptd = IntPtr.Zero; return 0x00040130; }
        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new COMException("Not implemented", unchecked((int)0x80004001));
        public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80004001); }
        public void DUnadvise(int connection) => throw new COMException("Not implemented", unchecked((int)0x80004001));
        public int EnumDAdvise(out IEnumSTATDATA? enumAdvise) { enumAdvise = null; return unchecked((int)0x80004001); }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class FormatEnumerator(params FORMATETC[] formats) : IEnumFORMATETC
    {
        private int _index;
        public int Next(int count, FORMATETC[] output, int[]? fetched)
        {
            var copied = 0;
            while (copied < count && _index < formats.Length) output[copied++] = formats[_index++];
            if (fetched is { Length: > 0 }) fetched[0] = copied;
            return copied == count ? 0 : 1;
        }
        public int Skip(int count) { _index = Math.Min(formats.Length, _index + count); return _index < formats.Length ? 0 : 1; }
        public int Reset() { _index = 0; return 0; }
        public void Clone(out IEnumFORMATETC clone) { var copy = new FormatEnumerator(formats) { _index = _index }; clone = copy; }
    }
}
