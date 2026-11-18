using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

public sealed class SystemMetricsService
{
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private long _previousReceived;
    private long _previousSent;
    private DateTimeOffset _previousNetworkSample = DateTimeOffset.Now;
    private bool _hasCpuSample;
    private bool _hasNetworkSample;

    public SystemUsage Read()
    {
        var cpu = ReadCpuPercent();
        var memory = ReadMemory();
        var network = ReadNetworkBytesPerSecond();

        return new SystemUsage(
            cpu,
            memory.RamPercent,
            memory.TotalBytes,
            memory.AvailableBytes,
            network.Download,
            network.Upload);
    }

    private double ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return 0;
        }

        var idle = ToUInt64(idleTime);
        var kernel = ToUInt64(kernelTime);
        var user = ToUInt64(userTime);

        if (!_hasCpuSample)
        {
            _previousIdle = idle;
            _previousKernel = kernel;
            _previousUser = user;
            _hasCpuSample = true;
            return 0;
        }

        var idleDelta = idle - _previousIdle;
        var kernelDelta = kernel - _previousKernel;
        var userDelta = user - _previousUser;
        var total = kernelDelta + userDelta;

        _previousIdle = idle;
        _previousKernel = kernel;
        _previousUser = user;

        if (total == 0)
        {
            return 0;
        }

        var busy = total - idleDelta;
        return ClampPercent(busy * 100.0 / total);
    }

    private static MemorySample ReadMemory()
    {
        var status = new MemoryStatusEx();
        status.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();

        if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
        {
            return new MemorySample(0, 0, 0);
        }

        var used = status.ullTotalPhys - status.ullAvailPhys;
        return new MemorySample(
            ClampPercent(used * 100.0 / status.ullTotalPhys),
            status.ullTotalPhys,
            status.ullAvailPhys);
    }

    private NetworkSample ReadNetworkBytesPerSecond()
    {
        long received = 0;
        long sent = 0;

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var stats = adapter.GetIPv4Statistics();
            received += stats.BytesReceived;
            sent += stats.BytesSent;
        }

        var now = DateTimeOffset.Now;
        if (!_hasNetworkSample)
        {
            _previousReceived = received;
            _previousSent = sent;
            _previousNetworkSample = now;
            _hasNetworkSample = true;
            return new NetworkSample(0, 0);
        }

        var seconds = Math.Max(0.2, (now - _previousNetworkSample).TotalSeconds);
        var download = Math.Max(0, received - _previousReceived) / seconds;
        var upload = Math.Max(0, sent - _previousSent) / seconds;

        _previousReceived = received;
        _previousSent = sent;
        _previousNetworkSample = now;

        return new NetworkSample(download, upload);
    }

    private static double ClampPercent(double value) => Math.Max(0, Math.Min(100, value));

    private static ulong ToUInt64(FileTime fileTime) => ((ulong)fileTime.dwHighDateTime << 32) | fileTime.dwLowDateTime;

    private sealed record MemorySample(double RamPercent, ulong TotalBytes, ulong AvailableBytes);

    private sealed record NetworkSample(double Download, double Upload);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
