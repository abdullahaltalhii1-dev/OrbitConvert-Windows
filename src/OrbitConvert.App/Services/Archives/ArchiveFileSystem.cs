using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OrbitConvert.App.Services.Archives;

/// <summary>Archive-only Windows path pins. Denied delete-sharing prevents ancestor replacement;
/// handle-relative no-follow IO avoids re-resolving ancestors through reparse points. Not an OS sandbox.</summary>
internal sealed class ArchiveFileSystem(string? externallyPinnedRoot = null) : IDisposable
{
    private readonly Dictionary<string, SafeFileHandle> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileStream> _files = [];
    public SafeFileHandle? PinDirectory(string path, bool rename = false)
    {
        path = Path.GetFullPath(path);
        if (_directories.TryGetValue(path, out var pinned)) { return pinned; }
        var parent = Path.GetDirectoryName(path.TrimEnd('\\'));
        var external = externallyPinnedRoot is not null && path.Equals(externallyPinnedRoot, StringComparison.OrdinalIgnoreCase);
        var parentHandle = parent is not null && !external ? PinDirectory(parent) : null;
        // FILE_LIST_DIRECTORY is essential: metadata-only opens do not enforce sharing restrictions.
        var access = 0x81u | (rename ? 0x10000u : 0);
        // Write-sharing is required by Windows rename internals. All child IO is handle-relative
        // with no-follow semantics, never a re-resolved path through these ancestors.
        var handle = parentHandle is null ? Open(path, access, 3, 0x02200000, external ? 7u : 3u)
            : OpenRelative(parentHandle, Path.GetFileName(path), access, 1, directory: true);
        try
        {
            Check(handle, path, true);
            _directories.Add(path, handle);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    public void MakeDirectory(string path, bool rename = false)
    {
        path = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(path)!;
        var parentHandle = PinDirectory(parent)!;
        var handle = OpenRelative(parentHandle, Path.GetFileName(path), 0x81u | (rename ? 0x10000u : 0), 2, directory: true);
        try { Check(handle, path, true); _directories.Add(path, handle); }
        catch { handle.Dispose(); throw; }
    }
    public void EnsureParents(string root, string relative, bool includeLeaf = false)
    {
        var parts = ArchivePaths.Validate(relative).Split('/');
        var current = root;
        PinDirectory(root);
        foreach (var part in parts.Take(includeLeaf ? parts.Length : parts.Length - 1))
        {
            current = Path.Combine(current, part);
            if (!_directories.ContainsKey(current))
            {
                var parent = PinDirectory(Path.GetDirectoryName(current)!)!;
                var handle = OpenRelative(parent, part, 0x81, 3, directory: true);
                try { Check(handle, current, true); _directories.Add(current, handle); }
                catch { handle.Dispose(); throw; }
            }
        }
    }
    public FileStream ReadFile(string path)
    {
        var parent = PinDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!)!;
        var handle = OpenRelative(parent, Path.GetFileName(path), 0x80000000, 1, directory: false);
        try { Check(handle, path, false); var stream = new FileStream(handle, FileAccess.Read); _files.Add(stream); return stream; }
        catch { handle.Dispose(); throw; }
    }
    public FileStream NewFile(string path, bool rename = false)
    {
        var parent = PinDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!)!;
        var handle = OpenRelative(parent, Path.GetFileName(path), 0xC0000000u | (rename ? 0x10000u : 0), 2, directory: false);
        try { Check(handle, path, false); var stream = new FileStream(handle, FileAccess.ReadWrite); _files.Add(stream); return stream; }
        catch { handle.Dispose(); throw; }
    }
    public static void Publish(SafeFileHandle source, SafeFileHandle parent, string name)
    {
        // Rename the held object, not a re-resolved source path. ReplaceIfExists remains false.
        if (Path.GetFileName(name) != name || name.Length == 0) { throw new ArchiveRejected(ArchiveError.InvalidDestination); }
        var bytes = Encoding.Unicode.GetBytes(name);
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var data = Marshal.AllocHGlobal(lengthOffset + 6 + bytes.Length);
        try
        {
            for (var i = 0; i < lengthOffset + 4; i++) { Marshal.WriteByte(data, i, 0); }
            Marshal.WriteIntPtr(data, rootOffset, parent.DangerousGetHandle());
            Marshal.WriteInt32(data, lengthOffset, bytes.Length);
            Marshal.Copy(bytes, 0, data + lengthOffset + 4, bytes.Length);
            Marshal.WriteInt16(data, lengthOffset + 4 + bytes.Length, 0);
            var status = NtSetInformationFile(source, out _, data, (uint)(lengthOffset + 6 + bytes.Length), 10);
            if (status < 0)
            {
                var error = (int)RtlNtStatusToDosError(status);
                if (error is 80 or 183) { throw new ArchiveRejected(ArchiveError.DestinationExists); }
                throw new IOException("No-replace finalization failed.", new Win32Exception(error));
            }
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    public void PublishDirectory(string root, SafeFileHandle parent, string name)
    {
        // Windows refuses directory rename while descendants are open. All writes/hash checks are
        // finished. Keep root/ancestors pinned and perform no descendant IO after this close barrier.
        foreach (var path in _directories.Keys.Where(x => x.Equals(root, StringComparison.OrdinalIgnoreCase) || x.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)))
        { Check(_directories[path], path, true); }
        foreach (var file in _files) { file.Dispose(); }
        foreach (var path in _directories.Keys.Where(x => x.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)).ToArray())
        { _directories[path].Dispose(); _directories.Remove(path); }
        Publish(_directories[root], parent, name);
    }
    public bool CleanupOwnedDirectory(string root)
    {
        // Keep the original reserved root pinned throughout cleanup. Never resolve a replacement root.
        foreach (var file in _files) { file.Dispose(); }
        foreach (var path in _directories.Keys.Where(x => x.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)).ToArray())
        { _directories[path].Dispose(); _directories.Remove(path); }
        if (!_directories.TryGetValue(root, out var owned)) { return false; }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                DeleteChildren(root, owned);
                DeleteHeld(owned);
                owned.Dispose(); _directories.Remove(root);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Thread.Sleep(100); }
        }
        return false;
    }
    public static bool CleanupOwnedFile(FileStream file)
    {
        try { DeleteHeld(file.SafeFileHandle); file.Dispose(); return true; }
        catch (IOException) { return false; }
    }
    private static void DeleteChildren(string root, SafeFileHandle parent)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            using var child = OpenRelative(parent, Path.GetFileName(path), 0x10081, 1, directory: null, cleanup: true);
            if (!GetFileInformationByHandle(child, out var info)) { throw new IOException("Cleanup metadata unavailable."); }
            // Delete a link itself; never follow it, even in an operation-owned tree.
            if ((info.Attributes & 0x410) == 0x10) { DeleteChildren(path, child); }
            DeleteHeld(child);
        }
    }
    private static void DeleteHeld(SafeFileHandle file)
    {
        var data = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(data, 1);
            if (!SetFileInformationByHandle(file, 4, data, 4)) { throw new IOException("Owned cleanup failed.", new Win32Exception(Marshal.GetLastWin32Error())); }
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    public void Dispose()
    {
        foreach (var file in _files) { file.Dispose(); }
        foreach (var directory in _directories.Values.Reverse()) { directory.Dispose(); }
    }
    private static SafeFileHandle Open(string path, uint access, uint creation, uint flags, uint sharing = 3)
    {
        var handle = CreateFile(path, access, sharing, IntPtr.Zero, creation, flags, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Safe file open failed.", new Win32Exception(Marshal.GetLastWin32Error())); }
        return handle;
    }
    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, uint access, uint disposition, bool? directory, bool cleanup = false)
    {
        if (Path.GetFileName(name) != name || name is "" or "." or "..") { throw new ArchiveRejected(ArchiveError.UnsafePath); }
        if (!cleanup && (!GetFileInformationByHandle(parent, out var parentInfo) || (parentInfo.Attributes & 0x400) != 0)) { throw new ArchiveRejected(ArchiveError.LinksNotSupported); }
        var text = Marshal.StringToHGlobalUni(name);
        var descriptor = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            var unicode = new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2 + 2)), Buffer = text };
            Marshal.StructureToPtr(unicode, descriptor, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), Root = parent.DangerousGetHandle(), Name = descriptor, Attributes = cleanup ? 0x40u : 0x1040u };
            var options = 0x00204020u | (directory == true ? 1u : directory == false ? 0x40u : 0);
            var status = NtCreateFile(out var file, access | 0x100000, ref attributes, out _, IntPtr.Zero, directory == true ? 0x10u : 0x80u,
                directory == false ? 1u : 3u, disposition, options, IntPtr.Zero, 0);
            if (status < 0)
            {
                file?.Dispose();
                if ((uint)status is 0xC000050B or 0xC0000279) { throw new ArchiveRejected(ArchiveError.LinksNotSupported); }
                throw new IOException("Handle-relative no-follow open failed.", new Win32Exception((int)RtlNtStatusToDosError(status)));
            }
            return file;
        }
        finally { Marshal.FreeHGlobal(descriptor); Marshal.FreeHGlobal(text); }
    }
    private static void Check(SafeFileHandle handle, string path, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory || !directory && info.Links != 1)
        { throw new ArchiveRejected(ArchiveError.LinksNotSupported); }
        var actual = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, actual, actual.Capacity, 0);
        if (length == 0 || length >= actual.Capacity || !actual.ToString().Replace("\\\\?\\", "", StringComparison.Ordinal).TrimEnd('\\').Equals(Path.GetFullPath(path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        { throw new ArchiveRejected(ArchiveError.UnsafePath); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatus { public IntPtr Status, Information; }
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes { public int Length; public IntPtr Root, Name; public uint Attributes; public IntPtr Security, Quality; }
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle file, uint access, ref ObjectAttributes attributes, out IoStatus status, IntPtr allocation, uint fileAttributes,
        uint sharing, uint disposition, uint options, IntPtr extendedAttributes, uint extendedLength);
    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatus ioStatus, IntPtr information, uint length, int informationClass);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, int length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr information, uint size);
}
