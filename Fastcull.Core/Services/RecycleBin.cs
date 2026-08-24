using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Fastcull.Services
{
    /// <summary>
    /// Moves files to the Windows Recycle Bin (PRD 2.1.2).
    ///
    /// Recycle Bin rather than <see cref="File.Delete"/>, and that distinction is load-bearing:
    /// PRD 1.9's undo stack is unbuilt, so within the app a deletion is final. The Recycle Bin's
    /// own restore is currently the *only* undo that exists for it, which is precisely why a
    /// permanent delete would be the wrong call here.
    ///
    /// Implemented over SHFileOperation rather than Microsoft.VisualBasic.FileIO, which would also
    /// work: this project's standing rule is no dependencies beyond PRD 5.2, and thirty lines of
    /// P/Invoke is cheaper than an argument about whether the VB assembly counts as one.
    /// </summary>
    public static class RecycleBin
    {
        private const uint FO_DELETE = 0x0003;

        // ALLOWUNDO is what makes this the Recycle Bin instead of a permanent delete - without it
        // SHFileOperation erases the file outright.
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_NOERRORUI = 0x0400;
        private const ushort FOF_SILENT = 0x0004;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;

            /// <summary>Must be DOUBLE-null-terminated: the API reads a list, not a string.</summary>
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string pTo;

            public ushort fFlags;

            /// <summary>Win32 BOOL. Declared as int rather than bool so its width is unambiguous.</summary>
            public int fAnyOperationsAborted;

            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SHFileOperationW(ref SHFILEOPSTRUCT fileOp);

        /// <summary>
        /// Sends one file to the Recycle Bin. Returns true only when the file is genuinely gone
        /// from its path afterwards.
        ///
        /// Never throws. A locked, read-only or already-missing file returns false and the caller
        /// leaves the sequence untouched - PRD 2.1.2 is explicit that a photo must not vanish from
        /// the filmstrip while surviving on disk.
        /// </summary>
        public static bool TrySend(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;

            try
            {
                if (!File.Exists(filePath)) return false;

                var op = new SHFILEOPSTRUCT
                {
                    hwnd = IntPtr.Zero,
                    wFunc = FO_DELETE,

                    // The extra '\0' is the list terminator. A single-null string here makes the
                    // API read past the end of the buffer for the next entry.
                    pFrom = filePath + "\0\0",
                    pTo = null!,
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
                    lpszProgressTitle = null!,
                };

                var result = SHFileOperationW(ref op);

                // Both have to hold: a non-zero result is a failure, and an aborted operation can
                // still return zero. The existence check is the one that actually settles it.
                if (result != 0 || op.fAnyOperationsAborted != 0) return false;

                return !File.Exists(filePath);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
