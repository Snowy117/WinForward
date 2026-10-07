using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinForward.E2E.Client;

/// <summary>
/// One process's counters as the OS reported them, under the process name they were read for.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly struct ProcessCounters
{
    internal ProcessCounters(
        double cpuSeconds,
        long privateBytes,
        long workingSet,
        long peakWorkingSet,
        long threads,
        long handles)
    {
        CpuSeconds = cpuSeconds;
        PrivateBytes = privateBytes;
        WorkingSet = workingSet;
        PeakWorkingSet = peakWorkingSet;
        Threads = threads;
        Handles = handles;
    }

    internal double CpuSeconds { get; }

    internal long PrivateBytes { get; }

    internal long WorkingSet { get; }

    internal long PeakWorkingSet { get; }

    internal long Threads { get; }

    internal long Handles { get; }
}

/// <summary>
/// Reads process counters from the OS, one process or one name at a time. Every read answers whether it
/// succeeded instead of throwing: a process that exited between enumeration and read, or a platform that
/// does not expose one of the counters, is a sample the record marks as unread rather than a failed tick.
/// </summary>
internal static class ProcessCounterSource
{
    private static bool TryRefresh(Process process)
    {
        try
        {
            process.Refresh();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryReadLong(Process process, Func<Process, long> read, out long value)
    {
        try
        {
            value = read(process);
            return true;
        }
        catch (InvalidOperationException)
        {
            value = 0;
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            value = 0;
            return false;
        }
        catch (Win32Exception)
        {
            value = 0;
            return false;
        }
        catch (NotSupportedException)
        {
            value = 0;
            return false;
        }
        catch (IOException)
        {
            value = 0;
            return false;
        }
    }

    internal static bool TryReadStartTime(Process process, out DateTime startedUtc)
    {
        try
        {
            startedUtc = process.StartTime;
            return true;
        }
        catch (InvalidOperationException)
        {
            startedUtc = default;
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            startedUtc = default;
            return false;
        }
        catch (Win32Exception)
        {
            startedUtc = default;
            return false;
        }
        catch (NotSupportedException)
        {
            startedUtc = default;
            return false;
        }
        catch (IOException)
        {
            startedUtc = default;
            return false;
        }
    }

    /// <summary>
    /// A false return means the summed counters are incomplete rather than zero; the counters still carry
    /// the reads that did succeed.
    /// </summary>
    internal static bool TryReadCounters(Process process, bool hasHandles, out ProcessCounters counters)
    {
        var read = TryRefresh(process);
        read &= TryReadLong(process, static instance => (long)instance.TotalProcessorTime.TotalMilliseconds, out var cpuMilliseconds);
        read &= TryReadLong(process, static instance => instance.PrivateMemorySize64, out var privateBytes);
        read &= TryReadLong(process, static instance => instance.WorkingSet64, out var workingSet);
        read &= TryReadLong(process, static instance => instance.PeakWorkingSet64, out var peakWorkingSet);
        read &= TryReadLong(process, static instance => instance.Threads.Count, out var threads);
        long handles = 0;
        if (hasHandles)
        {
            read &= TryReadLong(process, static instance => instance.HandleCount, out handles);
        }

        counters = new ProcessCounters(cpuMilliseconds / 1000.0, privateBytes, workingSet, peakWorkingSet, threads, handles);
        return read;
    }

    /// <summary>
    /// Every process the name matches, summed. A name that matches nothing yields empty totals rather
    /// than a failure: an absent process is a fact the record publishes as one.
    /// </summary>
    internal static ProcessTotals ReadProcessTotals(string name, bool hasHandles)
    {
        var totals = new ProcessTotals();
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(name);
        }
        catch (InvalidOperationException)
        {
            /* the process exited between enumeration and read */
            return totals;
        }
        catch (Win32Exception)
        {
            /* the process exited between enumeration and read */
            return totals;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                var countersRead = TryReadCounters(process, hasHandles, out var counters);
                var hasStartTime = TryReadStartTime(process, out var startedUtc);
                totals.Processes.Add(new SampledProcess(
                    process.Id,
                    hasStartTime ? startedUtc : null,
                    countersRead,
                    counters.CpuSeconds,
                    counters.PrivateBytes));
                totals.CpuSeconds += counters.CpuSeconds;
                totals.PrivateBytes += counters.PrivateBytes;
                totals.WorkingSet += counters.WorkingSet;
                totals.PeakWorkingSet += counters.PeakWorkingSet;
                totals.Threads += counters.Threads;
                totals.Handles += counters.Handles;
            }
        }

        return totals;
    }
}
