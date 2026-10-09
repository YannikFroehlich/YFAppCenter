using System;
using System.IO;
using System.Runtime.InteropServices;

namespace YFAppCenter.Setup;

/// <summary>
/// Moderner Windows-Dialog zur Ordnerauswahl. .NET Framework bringt nur den alten Baumdialog mit,
/// deshalb direkt über die Shell-Schnittstelle IFileDialog.
/// </summary>
public static class FolderPicker
{
    private const uint PickFolders = 0x20, ForceFileSystem = 0x40;
    private const uint FileSystemPath = 0x80058000;
    private const int Cancelled = unchecked((int)0x800704C7);

    public static string? Pick(IntPtr owner, string title, string? initialDirectory)
    {
        var dialog = (IFileDialog)new FileOpenDialog();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | PickFolders | ForceFileSystem);
            dialog.SetTitle(title);

            // Der gewünschte Ordner existiert oft noch nicht; dann beim nächsten vorhandenen Elternordner starten.
            var start = initialDirectory;
            while (start is not null && !Directory.Exists(start))
            {
                start = Path.GetDirectoryName(start);
            }

            if (start is not null && SHCreateItemFromParsingName(start, IntPtr.Zero, typeof(IShellItem).GUID, out var folder) == 0)
            {
                dialog.SetFolder(folder);
            }

            var result = dialog.Show(owner);
            if (result == Cancelled)
            {
                return null;
            }

            Marshal.ThrowExceptionForHR(result);
            dialog.GetResult(out var item);
            item.GetDisplayName(FileSystemPath, out var path);
            return path;
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    // Reihenfolge der Methoden muss der Vtable entsprechen; ungenutzte Einträge sind Platzhalter.
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes();
        void SetFileTypeIndex();
        void GetFileTypeIndex();
        void Advise();
        void Unadvise();
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder();
        void GetCurrentSelection();
        void SetFileName();
        void GetFileName();
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel();
        void SetFileNameLabel();
        void GetResult(out IShellItem item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler();
        void GetParent();
        void GetDisplayName(uint name, [MarshalAs(UnmanagedType.LPWStr)] out string path);
    }
}
