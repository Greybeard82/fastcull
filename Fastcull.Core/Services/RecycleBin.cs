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

        // ------------------------------------------------------------------
        // Restore (PRD 1.9's undo of a delete)
        // ------------------------------------------------------------------

        /// <summary>The Recycle Bin, as a shell namespace ordinal.</summary>
        private const int SsfBitBucket = 10;

        /// <summary>
        /// Documented property key for a recycled item's original folder. Used instead of
        /// GetDetailsOf(item, 1), whose column index and heading both move between Windows
        /// versions and locales.
        /// </summary>
        private const string OriginalLocationProperty = "{9B174B33-40FF-11D2-A27E-00C04FC30871} 2";

        /// <summary>
        /// The restore verb, in the languages this is likely to meet. **The verb name is
        /// localised**, so there is no single string to invoke - this machine reports it in
        /// Spanish, an English install reports "Restore", and hard-coding either would produce a
        /// feature that works only where it was written.
        ///
        /// Only names on this list are ever invoked. Iterating the item's verbs and trying each
        /// until something works would be shorter and is exactly what must not be done: one of
        /// those verbs is "Delete", and invoking it would permanently destroy the photograph the
        /// user is trying to recover.
        /// </summary>
        private static readonly string[] RestoreVerbs =
        [
            "ESTORE",           // the canonical accelerator form, locale-independent in practice
            "Restore", "Restaurar", "Restaurer", "Ripristina", "Wiederherstellen",
            "Herstellen", "Restaurera", "Gendan", "Palauta", "Przywróć", "Восстановить",
            "還原", "还原", "復元", "복원",
        ];

        /// <summary>
        /// Puts a recycled file back where it came from (PRD 1.9).
        ///
        /// Returns true only when the file is genuinely present at <paramref name="originalPath"/>
        /// afterwards - the same outcome-based check <see cref="TrySend"/> uses, and the reason
        /// this is trustworthy despite the localised verb: success is measured, not assumed from
        /// an API returning without error.
        ///
        /// Never throws. A Recycle Bin that has been emptied, a shell COM failure, or an item that
        /// cannot be matched all return false, and the caller reports it.
        ///
        /// Uses late-bound reflection over Shell.Application rather than a COM reference, because
        /// adding an interop assembly would be a dependency and PRD 5.2 governs those.
        /// </summary>
        public static bool TryRestore(string originalPath)
        {
            if (string.IsNullOrWhiteSpace(originalPath)) return false;

            try
            {
                // Already back - a double undo, or the user restored it by hand.
                if (File.Exists(originalPath)) return true;

                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is null) return false;

                var shell = Activator.CreateInstance(shellType);
                if (shell is null) return false;

                var bin = shellType.InvokeMember("NameSpace",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, [SsfBitBucket]);
                if (bin is null) return false;

                var items = bin.GetType().InvokeMember("Items",
                    System.Reflection.BindingFlags.InvokeMethod, null, bin, null);
                if (items is null) return false;

                var count = (int)items.GetType().InvokeMember("Count",
                    System.Reflection.BindingFlags.GetProperty, null, items, null)!;

                var wantedFolder = Path.GetDirectoryName(originalPath) ?? string.Empty;
                var wantedName = Path.GetFileName(originalPath);

                // Newest first: a path can have been recycled more than once, and the most recent
                // is the one this undo is for.
                for (var i = count - 1; i >= 0; i--)
                {
                    var item = items.GetType().InvokeMember("Item",
                        System.Reflection.BindingFlags.InvokeMethod, null, items, [i]);
                    if (item is null) continue;

                    if (!Matches(item, wantedFolder, wantedName)) continue;
                    if (TryInvokeRestore(item, originalPath)) return true;
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool Matches(object item, string wantedFolder, string wantedName)
        {
            try
            {
                var name = item.GetType().InvokeMember("Name",
                    System.Reflection.BindingFlags.GetProperty, null, item, null) as string;

                var folder = item.GetType().InvokeMember("ExtendedProperty",
                    System.Reflection.BindingFlags.InvokeMethod, null, item, [OriginalLocationProperty]) as string;

                if (name is null || folder is null) return false;
                if (!string.Equals(folder.TrimEnd('\\'), wantedFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return false;

                if (string.Equals(name, wantedName, StringComparison.OrdinalIgnoreCase)) return true;

                // Explorer hides known extensions, and the shell reports Name the same way, so a
                // recycled "shot01.jpg" can come back as "shot01". Comparing the stem as well is
                // what keeps this working regardless of that setting.
                return string.Equals(name, Path.GetFileNameWithoutExtension(wantedName), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryInvokeRestore(object item, string originalPath)
        {
            try
            {
                var verbs = item.GetType().InvokeMember("Verbs",
                    System.Reflection.BindingFlags.InvokeMethod, null, item, null);
                if (verbs is null) return false;

                var verbCount = (int)verbs.GetType().InvokeMember("Count",
                    System.Reflection.BindingFlags.GetProperty, null, verbs, null)!;

                for (var i = 0; i < verbCount; i++)
                {
                    var verb = verbs.GetType().InvokeMember("Item",
                        System.Reflection.BindingFlags.InvokeMethod, null, verbs, [i]);
                    if (verb is null) continue;

                    var raw = verb.GetType().InvokeMember("Name",
                        System.Reflection.BindingFlags.GetProperty, null, verb, null) as string;
                    if (raw is null) continue;

                    // "&Restaurar" -> "Restaurar". The ampersand is the menu mnemonic.
                    var name = raw.Replace("&", string.Empty).Trim();

                    if (!Array.Exists(RestoreVerbs, v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    verb.GetType().InvokeMember("DoIt",
                        System.Reflection.BindingFlags.InvokeMethod, null, verb, null);

                    // The shell performs this asynchronously, so the file is not necessarily back
                    // by the time DoIt returns. Poll briefly rather than reporting a false failure.
                    for (var wait = 0; wait < 40; wait++)
                    {
                        if (File.Exists(originalPath)) return true;
                        System.Threading.Thread.Sleep(25);
                    }
                }

                return File.Exists(originalPath);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
