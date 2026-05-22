using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DeployMonitor.Services
{
    /// <summary>
    /// 시스템 리소스(CPU, 메모리, GPU) 사용량을 주기적으로 수집.
    /// CPU/MEM은 메인 루프에서, GPU는 별도 백그라운드로 분리하여
    /// WMI hang 시에도 UI 갱신이 멈추지 않도록 함.
    /// </summary>
    public class SystemMonitorService : IDisposable
    {
        private CancellationTokenSource? _cts;
        private PerformanceCounter? _cpuCounter;
        private double _lastGpu = -1;
        private int _gpuQueryRunning;

        public event Action<double, double, double>? UsageUpdated;

        public void Start(int intervalMs = 3000)
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // 메인 루프: CPU + MEM (가벼움, 절대 hang 안 됨)
            Task.Run(async () =>
            {
                try
                {
                    _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                    _cpuCounter.NextValue();
                }
                catch
                {
                    _cpuCounter = null;
                }

                await Task.Delay(1500, token);

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var cpu = GetCpuUsage();
                        var mem = GetMemoryUsage();
                        UsageUpdated?.Invoke(cpu, mem, _lastGpu);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }

                    try { await Task.Delay(intervalMs, token); }
                    catch (OperationCanceledException) { break; }
                }
            }, token);

            // GPU 루프: 별도 스레드, 15초 간격, hang되어도 CPU/MEM에 영향 없음
            Task.Run(async () =>
            {
                await Task.Delay(3000, token); // 초기 지연

                while (!token.IsCancellationRequested)
                {
                    if (Interlocked.CompareExchange(ref _gpuQueryRunning, 1, 0) == 0)
                    {
                        try
                        {
                            var gpu = QueryGpuUsage(token);
                            _lastGpu = gpu;
                        }
                        catch { }
                        finally
                        {
                            Interlocked.Exchange(ref _gpuQueryRunning, 0);
                        }
                    }

                    try { await Task.Delay(15000, token); }
                    catch (OperationCanceledException) { break; }
                }
            }, token);
        }

        private double GetCpuUsage()
        {
            try { return _cpuCounter?.NextValue() ?? 0; }
            catch { return 0; }
        }

        private static double GetMemoryUsage()
        {
            try
            {
                var status = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(status))
                    return status.dwMemoryLoad;
            }
            catch { }
            return 0;
        }

        private static double QueryGpuUsage(CancellationToken token)
        {
            ManagementObjectSearcher? searcher = null;
            ManagementObjectCollection? results = null;
            try
            {
                // 방법 1: GPU Performance Counters (Windows 10 1709+)
                searcher = new ManagementObjectSearcher(
                    "root\\CIMV2",
                    "SELECT Name, PercentProcessorTime FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
                searcher.Options.Timeout = TimeSpan.FromSeconds(5);

                if (token.IsCancellationRequested) return -1;
                results = searcher.Get();

                double maxUsage = 0;
                bool found = false;
                foreach (ManagementObject obj in results)
                {
                    try
                    {
                        if (token.IsCancellationRequested) return -1;
                        var name = obj["Name"]?.ToString() ?? "";
                        if (name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                        {
                            var val = Convert.ToDouble(obj["PercentProcessorTime"]);
                            if (val > maxUsage) maxUsage = val;
                            found = true;
                        }
                    }
                    finally
                    {
                        obj.Dispose();
                    }
                }

                return found ? maxUsage : -1;
            }
            catch
            {
                return -1;
            }
            finally
            {
                results?.Dispose();
                searcher?.Dispose();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = 64;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cpuCounter?.Dispose();
        }
    }
}
