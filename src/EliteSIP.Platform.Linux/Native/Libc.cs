using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EliteSIP.Platform.Linux.Native;

/// <summary>Те вызовы libc, которых нет в .NET.</summary>
internal static partial class Libc
{
    private const string Library = "libc";

    internal const int LockExclusive = 2;
    internal const int LockNonBlocking = 4;

    /// <summary><c>flock(2)</c>: 0 — замок взят.</summary>
    [LibraryImport(Library, EntryPoint = "flock", SetLastError = true)]
    internal static partial int Flock(SafeFileHandle fd, int operation);

    internal const short PollIn = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    /// <summary><c>poll(2)</c> на одном дескрипторе.</summary>
    [LibraryImport(Library, EntryPoint = "poll", SetLastError = true)]
    internal static unsafe partial int Poll(PollFd* fds, nuint count, int timeoutMilliseconds);
}
