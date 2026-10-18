using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WinCSVTool.Core;

/// <summary>Writes a private sibling file, then atomically publishes it without weakening existing file metadata.</summary>
public sealed class AtomicFile : IDisposable
{
    private readonly string destination;
    private readonly string temporary;
    private readonly bool replacing;
    private bool committed;
    private bool disposed;
    public FileStream Stream { get; }

    public AtomicFile(string path)
    {
        destination = Path.GetFullPath(path);
        temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".wincsv-" + Guid.NewGuid().ToString("N") + ".tmp");
        replacing = File.Exists(destination);
        try
        {
            Stream = OperatingSystem.IsWindows() ? CreatePrivateWindowsFile(temporary, replacing && File.GetAttributes(destination).HasFlag(FileAttributes.Encrypted))
                : new FileStream(temporary, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 65536,
                    Options = FileOptions.SequentialScan, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                });
        }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
    }

    public void Commit(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (committed) throw new InvalidOperationException("出力はすでに保存されています。");
        token.ThrowIfCancellationRequested();
        Stream.Flush(flushToDisk: true);
        Stream.Dispose();
        token.ThrowIfCancellationRequested();
        if (replacing)
            // ReplaceFile preserves the destination's DACL, encryption and supported metadata.
            // Metadata merge errors must fail; do not fall back to delete/move-overwrite.
            File.Replace(temporary, destination, null, ignoreMetadataErrors: false);
        else File.Move(temporary, destination); // A concurrently created destination must not be overwritten.
        committed = true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Stream.Dispose(); }
        finally { if (!committed && File.Exists(temporary)) File.Delete(temporary); }
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreatePrivateWindowsFile(string path, bool encrypted)
    {
        // Supply the protected DACL to CreateFile itself: no inherited broad-access interval exists.
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new UnauthorizedAccessException("現在の利用者の SID を取得できません。");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;;FA;;;SY)(A;;FA;;;" + sid + ")", 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateFile(path, 0x40000000, 0, ref security, 1, 0x08000000u | (encrypted ? 0x4000u : 0x80u), IntPtr.Zero);
            if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            try { return new FileStream(handle, FileAccess.Write, 65536, false); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint length);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
