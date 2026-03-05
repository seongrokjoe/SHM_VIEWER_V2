using System.Runtime.InteropServices;

namespace ShmViewer.Core.Shm;

public class ShmNotFoundException : Exception
{
    public ShmNotFoundException(string shmName)
        : base($"Shared Memory '{shmName}'를 열 수 없습니다. 대상 프로세스가 실행 중인지 확인하세요.") { }
}

public class ShmReader
{
    private const uint FILE_MAP_READ = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr OpenFileMapping(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
        uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public byte[] ReadSnapshot(string shmName, int size)
    {
        var handle = OpenFileMapping(FILE_MAP_READ, false, shmName);
        if (handle == IntPtr.Zero)
            throw new ShmNotFoundException(shmName);

        IntPtr addr = IntPtr.Zero;
        try
        {
            addr = MapViewOfFile(handle, FILE_MAP_READ, 0, 0, (UIntPtr)size);
            if (addr == IntPtr.Zero)
                throw new InvalidOperationException($"MapViewOfFile 실패 (ErrorCode: {Marshal.GetLastWin32Error()})");

            var buffer = new byte[size];
            Marshal.Copy(addr, buffer, 0, size);
            return buffer;
        }
        finally
        {
            if (addr != IntPtr.Zero)
                UnmapViewOfFile(addr);
            CloseHandle(handle);
        }
    }

    public bool Exists(string shmName)
    {
        var handle = OpenFileMapping(FILE_MAP_READ, false, shmName);
        if (handle == IntPtr.Zero) return false;
        CloseHandle(handle);
        return true;
    }
}
