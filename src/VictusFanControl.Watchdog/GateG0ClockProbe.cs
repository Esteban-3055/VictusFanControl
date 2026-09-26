using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VictusFanControl.Watchdog;

internal static class GateG0ClockProbe
{
    private const string ModeArgument = "--gate-g0-clock-probe";
    private const string WakeAfterArgument = "--wake-after-seconds";
    private const string MinimumExcludedArgument = "--minimum-excluded-seconds";
    private const string TokenArgument = "--gate-g0-auto-s3-token";
    private const string RequiredToken = "88F8-G0-AUTO-S3";

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotSupported = 50;

    public static bool IsRequested(string[] args) =>
        args.Any(
            argument =>
                string.Equals(
                    argument,
                    ModeArgument,
                    StringComparison.Ordinal));

    public static async Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error)
    {
        if (!OperatingSystem.IsWindows())
        {
            await error.WriteLineAsync(
                "Gate G0 automatic S3 clock probe requires Windows.");
            return 2;
        }

        ProbeOptions options;
        try
        {
            options = Parse(args);
        }
        catch (ArgumentException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return 2;
        }

        if (!string.Equals(
                options.Token,
                RequiredToken,
                StringComparison.Ordinal))
        {
            await error.WriteLineAsync(
                $"Automatic S3 probe refused: explicit {TokenArgument} {RequiredToken} is required.");
            return 2;
        }

        IMonotonicClock clock = new WindowsMonotonicClock();

        var first = clock.Milliseconds;
        await Task.Delay(250).ConfigureAwait(false);
        var second = clock.Milliseconds;

        if (second < first)
        {
            await error.WriteLineAsync(
                $"Production monotonic clock moved backwards before S3 test: {first} -> {second}");
            return 1;
        }

        await output.WriteLineAsync();
        await output.WriteLineAsync(
            $"Preflight monotonic sample: {first} -> {second} ms");
        await output.WriteLineAsync();
        await output.WriteLineAsync(
            "AUTOMATIC S3 TEST: Windows will be suspended by this process.");
        await output.WriteLineAsync(
            $"A one-shot absolute UTC wake timer will request resume after about {options.WakeAfterSeconds} seconds.");
        await output.WriteLineAsync(
            "The probe does not open PawnIO, EC, HP WMI fan control, the watchdog pipe, or the lease journal.");
        await output.WriteLineAsync(
            "If the machine does not wake automatically, wait at least 30 seconds and wake it manually.");
        await output.WriteLineAsync();
        await output.WriteAsync(
            "Press Enter to arm the wake timer and suspend Windows: ");
        await output.FlushAsync().ConfigureAwait(false);

        if (await input.ReadLineAsync().ConfigureAwait(false) is null)
        {
            await error.WriteLineAsync(
                "Gate G0 probe could not read the arming confirmation.");
            return 2;
        }

        try
        {
            EnableShutdownPrivilege();
        }
        catch (Win32Exception ex)
        {
            await error.WriteLineAsync(
                $"Could not enable SeShutdownPrivilege: {ex.Message} ({ex.NativeErrorCode}).");
            return 1;
        }

        var wakeTimer = CreateWaitableTimer(
            IntPtr.Zero,
            true,
            null);

        if (wakeTimer == IntPtr.Zero)
        {
            var nativeError = Marshal.GetLastPInvokeError();
            await error.WriteLineAsync(
                $"CreateWaitableTimer failed: {new Win32Exception(nativeError).Message} ({nativeError}).");
            return 1;
        }

        try
        {
            var wakeTargetUtc =
                DateTimeOffset.UtcNow.AddSeconds(
                    options.WakeAfterSeconds);

            // IMPORTANT: on Windows 8+ a RELATIVE waitable timer does not count
            // time spent in low-power states. An ABSOLUTE UTC FILETIME is used
            // so the timer can expire while S3 is active and request a wake.
            var dueTime =
                wakeTargetUtc.UtcDateTime.ToFileTimeUtc();

            Marshal.SetLastPInvokeError(0);
            var timerArmed = SetWaitableTimer(
                wakeTimer,
                ref dueTime,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                true);
            var timerError = Marshal.GetLastPInvokeError();

            if (!timerArmed)
            {
                await error.WriteLineAsync(
                    $"SetWaitableTimer failed: {new Win32Exception(timerError).Message} ({timerError}).");
                return 1;
            }

            if (timerError == ErrorNotSupported)
            {
                await error.WriteLineAsync(
                    "SetWaitableTimer reports ERROR_NOT_SUPPORTED for automatic resume; refusing to suspend automatically.");
                return 1;
            }

            await output.WriteLineAsync();
            await output.WriteLineAsync(
                $"WAKE_TIMER_ARMED utc={wakeTargetUtc:O} type=absolute resume=true");

            var wallBefore = DateTimeOffset.UtcNow;
            var unbiasedBefore = clock.Milliseconds;

            await output.WriteLineAsync(
                $"SUSPEND_DISPATCH wall={wallBefore:O} unbiased_ms={unbiasedBefore}");
            await output.FlushAsync().ConfigureAwait(false);

            Marshal.SetLastPInvokeError(0);
            var suspendResult = SetSuspendState(
                0,
                0,
                0);
            var suspendError = Marshal.GetLastPInvokeError();

            if (suspendResult == 0)
            {
                await error.WriteLineAsync(
                    $"SetSuspendState failed: {new Win32Exception(suspendError).Message} ({suspendError}).");
                return 1;
            }

            var wallAfter = DateTimeOffset.UtcNow;
            var unbiasedAfter = clock.Milliseconds;

            if (unbiasedAfter < unbiasedBefore)
            {
                await error.WriteLineAsync(
                    $"Production monotonic clock moved backwards across S3: {unbiasedBefore} -> {unbiasedAfter}");
                return 1;
            }

            var wallElapsedMs =
                (wallAfter - wallBefore).TotalMilliseconds;
            var unbiasedElapsedMs =
                (double)(unbiasedAfter - unbiasedBefore);
            var excludedMs =
                wallElapsedMs - unbiasedElapsedMs;
            var minimumExcludedMs =
                checked(
                    (double)options.MinimumExcludedSeconds *
                    1000.0);

            await output.WriteLineAsync();
            await output.WriteLineAsync(
                $"RESUME_RETURN wall={wallAfter:O} unbiased_ms={unbiasedAfter}");
            await output.WriteLineAsync(
                $"Measured interval: wall +{wallElapsedMs:N0} ms; unbiased +{unbiasedElapsedMs:N0} ms; excluded ~{excludedMs:N0} ms.");

            if (excludedMs < minimumExcludedMs)
            {
                await error.WriteLineAsync(
                    $"Gate G0 physical clock semantics: FAIL - excluded interval {excludedMs:N0} ms is below required {minimumExcludedMs:N0} ms.");
                return 1;
            }

            await output.WriteLineAsync(
                "Gate G0 production clock measurement: PASS");
            await output.WriteLineAsync(
                "Wall time advanced across S3 while QueryUnbiasedInterruptTime excluded the sleeping interval.");
            return 0;
        }
        finally
        {
            _ = CancelWaitableTimer(wakeTimer);
            _ = CloseHandle(wakeTimer);
        }
    }

    private static ProbeOptions Parse(string[] args)
    {
        var wakeAfterSeconds = 20;
        var minimumExcludedSeconds = 10;
        string? token = null;
        var modeSeen = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case ModeArgument:
                    if (modeSeen)
                    {
                        throw new ArgumentException(
                            $"{ModeArgument} may be specified only once.");
                    }

                    modeSeen = true;
                    break;

                case WakeAfterArgument:
                    wakeAfterSeconds =
                        ReadBoundedInt(
                            args,
                            ref index,
                            WakeAfterArgument,
                            15,
                            120);
                    break;

                case MinimumExcludedArgument:
                    minimumExcludedSeconds =
                        ReadBoundedInt(
                            args,
                            ref index,
                            MinimumExcludedArgument,
                            5,
                            60);
                    break;

                case TokenArgument:
                    token = ReadValue(
                        args,
                        ref index,
                        TokenArgument);
                    break;

                default:
                    throw new ArgumentException(
                        $"Unknown Gate G0 clock probe argument: {args[index]}");
            }
        }

        if (!modeSeen)
        {
            throw new ArgumentException(
                $"{ModeArgument} is required.");
        }

        if (wakeAfterSeconds < minimumExcludedSeconds + 5)
        {
            throw new ArgumentException(
                $"{WakeAfterArgument} must be at least 5 seconds greater than {MinimumExcludedArgument}.");
        }

        return new ProbeOptions(
            wakeAfterSeconds,
            minimumExcludedSeconds,
            token);
    }

    private static int ReadBoundedInt(
        string[] args,
        ref int index,
        string option,
        int minimum,
        int maximum)
    {
        var rawValue = ReadValue(
            args,
            ref index,
            option);

        if (!int.TryParse(rawValue, out var value) ||
            value < minimum ||
            value > maximum)
        {
            throw new ArgumentException(
                $"{option} must be an integer from {minimum} through {maximum}.");
        }

        return value;
    }

    private static string ReadValue(
        string[] args,
        ref int index,
        string option)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException(
                $"Missing value for {option}.");
        }

        return args[index];
    }

    private static void EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(
                GetCurrentProcess(),
                TokenAdjustPrivileges | TokenQuery,
                out var token))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError());
        }

        try
        {
            if (!LookupPrivilegeValue(
                    null,
                    "SeShutdownPrivilege",
                    out var luid))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError());
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new LuidAndAttributes
                {
                    Luid = luid,
                    Attributes = SePrivilegeEnabled
                }
            };

            Marshal.SetLastPInvokeError(0);
            if (!AdjustTokenPrivileges(
                    token,
                    false,
                    ref privileges,
                    0,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError());
            }

            var nativeError =
                Marshal.GetLastPInvokeError();

            if (nativeError != 0)
            {
                throw new Win32Exception(
                    nativeError);
            }
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    private sealed record ProbeOptions(
        int WakeAfterSeconds,
        int MinimumExcludedSeconds,
        string? Token);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr CreateWaitableTimer(
        IntPtr lpTimerAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
        string? lpTimerName);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        IntPtr hTimer,
        ref long lpDueTime,
        int lPeriod,
        IntPtr pfnCompletionRoutine,
        IntPtr lpArgToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool fResume);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(
        IntPtr hTimer);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(
        IntPtr hObject);

    [DllImport(
        "kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport(
        "advapi32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport(
        "advapi32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(
        string? lpSystemName,
        string lpName,
        out Luid lpLuid);

    [DllImport(
        "advapi32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport(
        "powrprof.dll",
        SetLastError = true)]
    private static extern byte SetSuspendState(
        byte hibernate,
        byte forceCritical,
        byte disableWakeEvent);
}
