using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OrbitConvert.App.Services.Media;

public enum ProcessOutcome
{
    Exited,
    Cancelled,
    TimedOut,
    StartFailed,
}

/// <param name="InactivityTimeout">Terminates the process when it writes nothing to standard output for this long.</param>
/// <param name="StandardOutputLine">Receives each standard-output line; when set, lines are not buffered.</param>
/// <param name="StartTimeout">Process creation can block (for example while security software scans a new executable).</param>
public sealed record ProcessRunRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    TimeSpan? InactivityTimeout = null,
    Action<string>? StandardOutputLine = null,
    TimeSpan? StartTimeout = null,
    long? MaximumJobMemoryBytes = null);

/// <param name="TerminationConfirmed">False when a stopped process did not finish terminating within the grace period.</param>
public sealed record ProcessRunResult(ProcessOutcome Outcome, int ExitCode, string StandardOutput, string StandardError,
    bool TerminationConfirmed = true)
{
    public bool Succeeded => TerminationConfirmed && Outcome == ProcessOutcome.Exited && ExitCode == 0;
}

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Runs a trusted executable directly (no shell) with an argument list, captures bounded output without deadlocks,
/// and terminates the whole owned process tree on cancellation or timeout. Once assignment is confirmed, a Windows
/// job object provides kill-on-close protection. Failed containment stops the child and fails the run.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    internal const int MaximumCapturedChars = 256 * 1024;
    internal static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan TerminationGrace = TimeSpan.FromSeconds(10);
    private readonly Func<Process, bool> _start;
    private readonly Func<IntPtr, IntPtr, bool>? _assignToJob;

    public ProcessRunner()
        : this(process => process.Start())
    {
    }

    /// <summary>Test seams for process creation and the native job-assignment call.</summary>
    internal ProcessRunner(Func<Process, bool> start, Func<IntPtr, IntPtr, bool>? assignToJob = null)
    {
        _start = start;
        _assignToJob = assignToJob;
    }

    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested) { return new(ProcessOutcome.Cancelled, -1, string.Empty, string.Empty); }
        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(request.ExecutablePath) ?? string.Empty,
        };
        foreach (var argument in request.Arguments) { startInfo.ArgumentList.Add(argument); }

        var process = new Process { StartInfo = startInfo };
        if (request.MaximumJobMemoryBytes is <= 0) { process.Dispose(); throw new ArgumentOutOfRangeException(nameof(request)); }
        var job = JobObject.TryCreate(request.MaximumJobMemoryBytes);
        if (job is null)
        {
            process.Dispose();
            return new(ProcessOutcome.StartFailed, -1, string.Empty, "Windows job containment could not be initialized.");
        }
        // Process.Start is synchronous and has been observed to block indefinitely inside process creation while
        // security software inspects a new executable. Never let that hang the conversion.
        var starting = Task.Run(() => _start(process));
        var startTimeout = request.StartTimeout ?? DefaultStartTimeout;
        await Task.WhenAny(starting, Task.Delay(startTimeout, cancellationToken)).ConfigureAwait(false);
        if (!starting.IsCompleted)
        {
            _ = starting.ContinueWith(_ => Abandon(process, job), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return new(cancellationToken.IsCancellationRequested ? ProcessOutcome.Cancelled : ProcessOutcome.TimedOut, -1, string.Empty,
                $"The process did not start within {startTimeout.TotalSeconds:0} s.", TerminationConfirmed: false);
        }
        try
        {
            if (!await starting.ConfigureAwait(false))
            {
                Release(process, job);
                return new(ProcessOutcome.StartFailed, -1, string.Empty, string.Empty);
            }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Release(process, job);
            return new(ProcessOutcome.StartFailed, -1, string.Empty, error.Message);
        }
        try { return await MonitorAsync(process, job, request, cancellationToken).ConfigureAwait(false); }
        finally { Release(process, job); }
    }

    private async Task<ProcessRunResult> MonitorAsync(Process process, JobObject job, ProcessRunRequest request, CancellationToken cancellationToken)
    {
        string? containmentError = null;
        try { job.Assign(process, _assignToJob); }
        catch (Win32Exception error) { containmentError = error.Message; }
        process.StandardInput.Close(); // The tools never need interactive input.

        var lastActivity = Stopwatch.StartNew();
        var standardOutput = new BoundedText(request.StandardOutputLine is null ? MaximumCapturedChars : 0);
        var standardError = new BoundedText(MaximumCapturedChars);
        var outputTask = PumpAsync(process.StandardOutput, line =>
        {
            lastActivity.Restart();
            standardOutput.AppendLine(line);
            request.StandardOutputLine?.Invoke(line);
        });
        var errorTask = PumpAsync(process.StandardError, standardError.AppendLine);

        var outcome = containmentError is null ? ProcessOutcome.Exited : ProcessOutcome.StartFailed;
        var deadline = Stopwatch.StartNew();
        var exited = process.WaitForExitAsync(CancellationToken.None);
        while (outcome == ProcessOutcome.Exited && !exited.IsCompleted)
        {
            if (cancellationToken.IsCancellationRequested) { outcome = ProcessOutcome.Cancelled; break; }
            if (deadline.Elapsed > request.Timeout ||
                (request.InactivityTimeout is { } idle && lastActivity.Elapsed > idle))
            {
                outcome = ProcessOutcome.TimedOut;
                break;
            }
            // A cancelled delay completes immediately; WhenAny never throws.
            await Task.WhenAny(exited, Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken)).ConfigureAwait(false);
        }

        var terminated = true;
        if (outcome != ProcessOutcome.Exited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // Already exited.
            catch (Win32Exception) { }
            // Observe termination before reporting or cleaning up, but never wait forever: a process frozen by other
            // software may not finish terminating. The job object is the second, independent way to end it.
            terminated = await CompletesWithin(exited, TerminationGrace).ConfigureAwait(false);
            if (!terminated)
            {
                job.Terminate();
                terminated = await CompletesWithin(exited, TerminationGrace).ConfigureAwait(false);
            }
        }
        // Pipes reach end-of-file once the process (and anything that inherited them) has exited.
        await CompletesWithin(Task.WhenAll(outputTask, errorTask), terminated ? TerminationGrace : TimeSpan.Zero).ConfigureAwait(false);
        var diagnostic = containmentError is null ? standardError.ToString() : containmentError + Environment.NewLine + standardError;
        return new(outcome, terminated ? process.ExitCode : -1, standardOutput.ToString(), diagnostic, terminated);
    }

    private static async Task<bool> CompletesWithin(Task task, TimeSpan limit) =>
        task.IsCompleted || await Task.WhenAny(task, Task.Delay(limit)).ConfigureAwait(false) == task;

    private static void Release(Process process, JobObject? job)
    {
        process.Dispose();
        job?.Dispose();
    }

    /// <summary>A start that completes after the caller gave up: terminate whatever started, then release it.</summary>
    private static void Abandon(Process process, JobObject? job)
    {
        try
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            process.WaitForExit(5000);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException) { }
        finally { Release(process, job); }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) { onLine(line); }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    /// <summary>Keeps at most the newest <c>limit</c> characters of a stream.</summary>
    private sealed class BoundedText(int limit)
    {
        private readonly StringBuilder _text = new();
        private readonly object _gate = new();

        public void AppendLine(string line)
        {
            if (limit == 0) { return; }
            lock (_gate)
            {
                _text.AppendLine(line);
                if (_text.Length > limit) { _text.Remove(0, _text.Length - limit); }
            }
        }

        public override string ToString() { lock (_gate) { return _text.ToString(); } }
    }

    /// <summary>Kill-on-close protection for processes successfully assigned to this job.</summary>
    private sealed class JobObject : IDisposable
    {
        private const int ExtendedLimitInformation = 9;
        private const uint KillOnJobClose = 0x2000;
        private readonly IntPtr _handle;

        private JobObject(IntPtr handle) => _handle = handle;

        public static JobObject? TryCreate(long? maximumMemoryBytes)
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) { return null; }
            var info = new JobObjectExtendedLimitInformation { BasicLimitInformation = { LimitFlags = KillOnJobClose } };
            if (maximumMemoryBytes is { } memory)
            {
                info.BasicLimitInformation.LimitFlags |= 0x200; // JOB_OBJECT_LIMIT_JOB_MEMORY: committed memory across this job.
                info.JobMemoryLimit = new UIntPtr(checked((ulong)memory));
            }
            var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(handle, ExtendedLimitInformation, buffer, (uint)length))
                {
                    CloseHandle(handle);
                    return null;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return new JobObject(handle);
        }

        public void Assign(Process process, Func<IntPtr, IntPtr, bool>? assign = null)
        {
            try
            {
                var assigned = (assign ?? AssignProcessToJobObject)(_handle, process.Handle);
                var error = assigned ? 0 : Marshal.GetLastWin32Error();
                // A short-lived tool can exit before assignment. Only an observed exit makes failure harmless.
                if (!assigned && !process.HasExited) { throw new Win32Exception(error, $"Windows job assignment failed (error {error})."); }
            }
            catch (InvalidOperationException) when (process.HasExited) { }
        }

        public void Terminate() => TerminateJobObject(_handle, 1);

        public void Dispose() => CloseHandle(_handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
