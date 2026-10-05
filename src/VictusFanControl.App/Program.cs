using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--product-gui-self-test")
        {
            ApplicationConfiguration.Initialize();
            Environment.ExitCode = ProductGuiSelfTest.Run(); return;
        }
        if (args.Length == 1 && args[0] == "--wmi-fan-gui-self-test")
        {
            Environment.ExitCode = WmiFanGuiIntegrationSelfTest.RunAsync().GetAwaiter().GetResult();
            return;
        }
        if (args.Length == 2 && args[0] == "--wmi-fan-fixture-owner")
        {
            Environment.ExitCode = WmiFanGuiIntegrationSelfTest.RunFixtureOwnerAsync(args[1]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length == 1 && args[0] == "--performance-gui-self-test")
        {
            Environment.ExitCode = PerformanceGuiIntegrationSelfTest.RunAsync().GetAwaiter().GetResult();
            return;
        }
        if (args.Length == 1 && args[0] == "--telemetry-coordination-self-test")
        {
            Environment.ExitCode = TelemetryCoordinationSelfTest.RunAsync().GetAwaiter().GetResult();
            return;
        }
        if (args.Length == 1 && args[0] == "--dashboard-self-test")
        {
            ApplicationConfiguration.Initialize();
            Environment.ExitCode = DashboardSelfTest.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--curve-editor-self-test")
        {
            ApplicationConfiguration.Initialize();
            Environment.ExitCode = AdaptiveCurveEditorSelfTest.Run();
            return;
        }

        if (args.Length == 1 &&
            args[0] == "--performance-settings-self-test")
        {
            Environment.ExitCode =
                PerformanceUiSettingsSelfTest.Run(
                    Console.Out);
            return;
        }

        if (args.Contains("--automatic-performance-limits") && !args.Contains("--8c40-automatic-final-qualification"))
            throw new ArgumentException("--automatic-performance-limits requires the Automatic qualification entry point.");

        AppLog.Initialize();

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            AppLog.Write($"UNHANDLED PROCESS EXCEPTION: {eventArgs.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            AppLog.Write($"UNOBSERVED TASK EXCEPTION: {eventArgs.Exception}");
            eventArgs.SetObserved();
        };

        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: @"Local\VictusFanControl.App",
            createdNew: out var createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "VictusFanControl is already running in this Windows session.",
                "VictusFanControl",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, eventArgs) =>
        {
            AppLog.Write($"UI THREAD EXCEPTION: {eventArgs.Exception}");
            MessageBox.Show(
                "VictusFanControl encountered an unexpected UI error. The safety supervisor will restore HP firmware authority if custom fan control is active. See the persistent application log for details.",
                "VictusFanControl",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        };

        var suspendHardwareTest = args.Any(
            arg => string.Equals(
                arg,
                "--suspend-custom-test",
                StringComparison.OrdinalIgnoreCase));

        var suspendHardwareTestToken = ReadOptionValue(
            args,
            "--suspend-test-token");

        var gateDHardwareTest = args.Any(
            arg => string.Equals(
                arg,
                "--gate-d-custom-test",
                StringComparison.OrdinalIgnoreCase));

        var gateDHardwareTestToken = ReadOptionValue(
            args,
            "--gate-d-test-token");

        var gateEHardwareTest = args.Any(
            arg => string.Equals(
                arg,
                "--gate-e-watchdog-death-test",
                StringComparison.OrdinalIgnoreCase));

        var gateEHardwareTestToken = ReadOptionValue(
            args,
            "--gate-e-test-token");

        var gateF1HardwareTest = args.Any(
            arg => string.Equals(
                arg,
                "--gate-f1-owned-double-death-test",
                StringComparison.OrdinalIgnoreCase));

        var gateF1HardwareTestToken = ReadOptionValue(
            args,
            "--gate-f1-test-token");

        var gateF2HardwareTest = args.Any(
            arg => string.Equals(
                arg,
                "--gate-f2-write-armed-double-death-test",
                StringComparison.OrdinalIgnoreCase));

        var gateF2HardwareTestToken = ReadOptionValue(
            args,
            "--gate-f2-test-token");

        var gateG1HardwareTest = args.Any(
            arg => string.Equals(
                arg,
                "--gate-g1-suspend-test",
                StringComparison.OrdinalIgnoreCase));

        var gateG1HardwareTestToken = ReadOptionValue(
            args,
            "--gate-g1-test-token");

        var gateG2HardwareTest = args.Any(
            arg => string.Equals(
                arg,
                "--gate-g2-suspend-repeat-test",
                StringComparison.OrdinalIgnoreCase));

        var gateG2HardwareTestToken = ReadOptionValue(
            args,
            "--gate-g2-test-token");

        var m6ModernStandbyHardwareTest = args.Any(
            arg => string.Equals(
                arg,
                "--8c40-m6-modern-standby-test",
                StringComparison.OrdinalIgnoreCase));

        var m6ModernStandbyHardwareTestToken = ReadOptionValue(
            args,
            "--8c40-m6-test-token");

        var m7HibernationHardwareTest = args.Any(
            arg => string.Equals(
                arg,
                "--8c40-m7-hibernation-test",
                StringComparison.OrdinalIgnoreCase));

        var m7HibernationHardwareTestToken = ReadOptionValue(
            args,
            "--8c40-m7-test-token");

        var m9dProductionLifecycleHardwareTest = args.Any(
            arg => string.Equals(
                arg,
                "--8c40-m9d-production-lifecycle-test",
                StringComparison.OrdinalIgnoreCase));

        var m9dProductionLifecycleHardwareTestToken = ReadOptionValue(
            args,
            "--8c40-m9d-test-token");

        var m9dProductionLifecycleMarkerRoot = ReadOptionValue(
            args,
            "--8c40-m9d-marker-root");

        var p15cGuiManualHardwareTest = args.Any(
            arg => string.Equals(
                arg,
                "--8c40-p15c-gui-manual-test",
                StringComparison.OrdinalIgnoreCase));

        var p15cGuiManualHardwareTestToken = ReadOptionValue(
            args,
            "--8c40-p15c-test-token");

        var p15cGuiManualMarkerRoot = ReadOptionValue(
            args,
            "--8c40-p15c-marker-root");

        var p15d1TrayExitHardwareTest = args.Any(
            arg => string.Equals(
                arg,
                "--8c40-p15d1-tray-exit-test",
                StringComparison.OrdinalIgnoreCase));

        var p15d1TrayExitHardwareTestToken = ReadOptionValue(
            args,
            "--8c40-p15d1-test-token");

        var p15d1TrayExitMarkerRoot = ReadOptionValue(
            args,
            "--8c40-p15d1-marker-root");

        var p15d2VariableManualHardwareTest = args.Any(
            arg => string.Equals(
                arg,
                "--8c40-p15d2-variable-manual-test",
                StringComparison.OrdinalIgnoreCase));

        var p15d2VariableManualHardwareTestToken = ReadOptionValue(
            args,
            "--8c40-p15d2-test-token");

        var p15d2VariableManualMarkerRoot = ReadOptionValue(
            args,
            "--8c40-p15d2-marker-root");

        var automaticFinalQualificationHardwareTest = args.Any(
            arg => string.Equals(
                arg,
                "--8c40-automatic-final-qualification",
                StringComparison.OrdinalIgnoreCase));

        var automaticFinalQualificationToken = ReadOptionValue(
            args,
            "--8c40-automatic-test-token");

        var automaticFinalQualificationMarkerRoot = ReadOptionValue(
            args,
            "--8c40-automatic-marker-root");

        var block1Test = args.Contains("--8c40-block1-test");
        var block1Token = ReadOptionValue(args, "--8c40-block1-token");
        var block1Root = ReadOptionValue(args, "--8c40-block1-root");
        if (block1Test ? block1Token != Block1QualificationSequence.RequiredToken || string.IsNullOrWhiteSpace(block1Root)
            : block1Token is not null || block1Root is not null)
            throw new ArgumentException("Block1 requires --8c40-block1-test --8c40-block1-token 8C40-WMI-BLOCK1 --8c40-block1-root PATH.");
        if (block1Test) block1Root = Path.GetFullPath(block1Root!);

        var hardwareTestModeCount =
            (suspendHardwareTest ? 1 : 0) +
            (gateDHardwareTest ? 1 : 0) +
            (gateEHardwareTest ? 1 : 0) +
            (gateF1HardwareTest ? 1 : 0) +
            (gateF2HardwareTest ? 1 : 0) +
            (gateG1HardwareTest ? 1 : 0) +
            (gateG2HardwareTest ? 1 : 0) +
            (m6ModernStandbyHardwareTest ? 1 : 0) +
            (m7HibernationHardwareTest ? 1 : 0) +
            (m9dProductionLifecycleHardwareTest ? 1 : 0) +
            (p15cGuiManualHardwareTest ? 1 : 0) +
            (p15d1TrayExitHardwareTest ? 1 : 0) +
            (p15d2VariableManualHardwareTest ? 1 : 0) +
            (automaticFinalQualificationHardwareTest ? 1 : 0) + (block1Test ? 1 : 0);

        if (hardwareTestModeCount > 1)
        {
            AppLog.Write(
                "Startup refused: suspend, Gate D, Gate E, Gate F1, Gate F2, Gate G1, Gate G2, HP 8C40 M6, M7, M9D, P15C, P15D1, P15D2 and final Automatic qualification modes are mutually exclusive.");
            Environment.ExitCode = 60;
            return;
        }

        if (automaticFinalQualificationHardwareTest)
        {
            if (!Hp8C40AutomaticFinalQualificationGate.PhysicalExecutionAuthorized ||
                !Hp8C40AutomaticFinalQualificationGate.NormalUserAutomaticRemainsClosed())
            {
                AppLog.Write(
                    "HP 8C40 final Automatic qualification refused: dedicated qualification gate is closed or normal user Automatic is already open.");
                Environment.ExitCode = 60;
                return;
            }

            if (!string.Equals(
                    automaticFinalQualificationToken,
                    Hp8C40AutomaticFinalQualificationGate.RequiredToken,
                    StringComparison.Ordinal))
            {
                AppLog.Write(
                    $"HP 8C40 final Automatic qualification refused: explicit --8c40-automatic-test-token {Hp8C40AutomaticFinalQualificationGate.RequiredToken} is required.");
                Environment.ExitCode = 60;
                return;
            }

            if (string.IsNullOrWhiteSpace(
                    automaticFinalQualificationMarkerRoot))
            {
                AppLog.Write(
                    "HP 8C40 final Automatic qualification requires --8c40-automatic-marker-root for isolated evidence.");
                Environment.ExitCode = 60;
                return;
            }

            automaticFinalQualificationMarkerRoot =
                Path.GetFullPath(
                    automaticFinalQualificationMarkerRoot);
        }
        else if (automaticFinalQualificationToken is not null ||
                 automaticFinalQualificationMarkerRoot is not null)
        {
            AppLog.Write(
                "Startup refused: final Automatic qualification token/marker options are valid only with --8c40-automatic-final-qualification.");
            Environment.ExitCode = 60;
            return;
        }

        if (suspendHardwareTest &&
            !string.Equals(
                suspendHardwareTestToken,
                "88F8-SUSPEND30",
                StringComparison.Ordinal))
        {
            AppLog.Write(
                "Suspend/custom hardware test refused: explicit --suspend-test-token 88F8-SUSPEND30 is required.");
            Environment.ExitCode = 60;
            return;
        }

        if (!suspendHardwareTest && suspendHardwareTestToken is not null)
        {
            AppLog.Write(
                "Startup refused: --suspend-test-token is valid only with --suspend-custom-test.");
            Environment.ExitCode = 60;
            return;
        }

        if (gateDHardwareTest &&
            !string.Equals(
                gateDHardwareTestToken,
                "88F8-GATED30",
                StringComparison.Ordinal))
        {
            AppLog.Write(
                "Gate D hardware test refused: explicit --gate-d-test-token 88F8-GATED30 is required.");
            Environment.ExitCode = 60;
            return;
        }

        if (!gateDHardwareTest && gateDHardwareTestToken is not null)
        {
            AppLog.Write(
                "Startup refused: --gate-d-test-token is valid only with --gate-d-custom-test.");
            Environment.ExitCode = 60;
            return;
        }

        if (gateEHardwareTest &&
            !string.Equals(
                gateEHardwareTestToken,
                "88F8-GATEE30",
                StringComparison.Ordinal))
        {
            AppLog.Write(
                "Gate E hardware test refused: explicit --gate-e-test-token 88F8-GATEE30 is required.");
            Environment.ExitCode = 60;
            return;
        }

        if (!gateEHardwareTest && gateEHardwareTestToken is not null)
        {
            AppLog.Write(
                "Startup refused: --gate-e-test-token is valid only with --gate-e-watchdog-death-test.");
            Environment.ExitCode = 60;
            return;
        }

        if (gateF1HardwareTest &&
            !string.Equals(
                gateF1HardwareTestToken,
                "88F8-GATEF1-30",
                StringComparison.Ordinal))
        {
            AppLog.Write(
                "Gate F1 hardware test refused: explicit --gate-f1-test-token 88F8-GATEF1-30 is required.");
            Environment.ExitCode = 60;
            return;
        }

        if (!gateF1HardwareTest && gateF1HardwareTestToken is not null)
        {
            AppLog.Write(
                "Startup refused: --gate-f1-test-token is valid only with --gate-f1-owned-double-death-test.");
            Environment.ExitCode = 60;
            return;
        }

        if (gateF2HardwareTest &&
            !string.Equals(
                gateF2HardwareTestToken,
                "88F8-GATEF2-30",
                StringComparison.Ordinal))
        {
            AppLog.Write(
                "Gate F2 hardware test refused: explicit --gate-f2-test-token 88F8-GATEF2-30 is required.");
            Environment.ExitCode = 60;
            return;
        }

        if (!gateF2HardwareTest && gateF2HardwareTestToken is not null)
        {
            AppLog.Write(
                "Startup refused: --gate-f2-test-token is valid only with --gate-f2-write-armed-double-death-test.");
            Environment.ExitCode = 60;
            return;
        }

        if (gateG1HardwareTest &&
            !string.Equals(
                gateG1HardwareTestToken,
                "88F8-GATEG1-30",
                StringComparison.Ordinal))
        {
            AppLog.Write(
                "Gate G1 hardware test refused: explicit --gate-g1-test-token 88F8-GATEG1-30 is required.");
            Environment.ExitCode = 60;
            return;
        }

        if (!gateG1HardwareTest && gateG1HardwareTestToken is not null)
        {
            AppLog.Write(
                "Startup refused: --gate-g1-test-token is valid only with --gate-g1-suspend-test.");
            Environment.ExitCode = 60;
            return;
        }

        if (gateG2HardwareTest &&
            !string.Equals(
                gateG2HardwareTestToken,
                "88F8-GATEG2-30",
                StringComparison.Ordinal))
        {
            AppLog.Write(
                "Gate G2 hardware test refused: explicit --gate-g2-test-token 88F8-GATEG2-30 is required.");
            Environment.ExitCode = 60;
            return;
        }

        if (!gateG2HardwareTest && gateG2HardwareTestToken is not null)
        {
            AppLog.Write(
                "Startup refused: --gate-g2-test-token is valid only with --gate-g2-suspend-repeat-test.");
            Environment.ExitCode = 60;
            return;
        }

        if (m6ModernStandbyHardwareTest &&
            !string.Equals(
                m6ModernStandbyHardwareTestToken,
                Hp8C40FanControlBackend.LifecycleQualificationToken,
                StringComparison.Ordinal))
        {
            AppLog.Write(
                $"HP 8C40 M6 Modern Standby test refused: explicit --8c40-m6-test-token {Hp8C40FanControlBackend.LifecycleQualificationToken} is required.");
            Environment.ExitCode = 60;
            return;
        }

        if (!m6ModernStandbyHardwareTest &&
            m6ModernStandbyHardwareTestToken is not null)
        {
            AppLog.Write(
                "Startup refused: --8c40-m6-test-token is valid only with --8c40-m6-modern-standby-test.");
            Environment.ExitCode = 60;
            return;
        }

        if (m7HibernationHardwareTest &&
            !string.Equals(
                m7HibernationHardwareTestToken,
                "8C40-M7-HIBERNATION30",
                StringComparison.Ordinal))
        {
            AppLog.Write(
                "HP 8C40 M7 hibernation test refused: explicit --8c40-m7-test-token 8C40-M7-HIBERNATION30 is required.");
            Environment.ExitCode = 60;
            return;
        }

        if (!m7HibernationHardwareTest &&
            m7HibernationHardwareTestToken is not null)
        {
            AppLog.Write(
                "Startup refused: --8c40-m7-test-token is valid only with --8c40-m7-hibernation-test.");
            Environment.ExitCode = 60;
            return;
        }

        if (m9dProductionLifecycleHardwareTest)
        {
            if (!Hp8C40M9DProductionLifecycleQualificationTest.PhysicalExecutionAuthorized ||
                !Hp8C40ProductionWatchdogGate.M9DPhysicalQualificationConstructionAuthorized)
            {
                AppLog.Write(
                    "HP 8C40 M9D production lifecycle refused: physical/controller construction gates remain closed.");
                Environment.ExitCode = 60;
                return;
            }

            if (!string.Equals(
                    m9dProductionLifecycleHardwareTestToken,
                    Hp8C40M9DProductionLifecycleQualificationTest.RequiredToken,
                    StringComparison.Ordinal))
            {
                AppLog.Write(
                    $"HP 8C40 M9D production lifecycle refused: explicit --8c40-m9d-test-token {Hp8C40M9DProductionLifecycleQualificationTest.RequiredToken} is required.");
                Environment.ExitCode = 60;
                return;
            }
        }
        else if (m9dProductionLifecycleHardwareTestToken is not null)
        {
            AppLog.Write(
                "Startup refused: --8c40-m9d-test-token is valid only with --8c40-m9d-production-lifecycle-test.");
            Environment.ExitCode = 60;
            return;
        }

        if (m9dProductionLifecycleHardwareTest &&
            string.IsNullOrWhiteSpace(m9dProductionLifecycleMarkerRoot))
        {
            AppLog.Write(
                "HP 8C40 M9D production lifecycle requires --8c40-m9d-marker-root for isolated evidence.");
            Environment.ExitCode = 60;
            return;
        }

        if (!m9dProductionLifecycleHardwareTest &&
            m9dProductionLifecycleMarkerRoot is not null)
        {
            AppLog.Write(
                "Startup refused: --8c40-m9d-marker-root is valid only with --8c40-m9d-production-lifecycle-test.");
            Environment.ExitCode = 60;
            return;
        }

        if (m9dProductionLifecycleMarkerRoot is not null)
        {
            m9dProductionLifecycleMarkerRoot =
                Path.GetFullPath(m9dProductionLifecycleMarkerRoot);
        }

        if (p15cGuiManualHardwareTest)
        {
            if (!Hp8C40P15CGuiManualQualificationGate.PhysicalExecutionAuthorized)
            {
                AppLog.Write(
                    "HP 8C40 P15C real-GUI Manual qualification refused: dedicated physical gate remains closed.");
                Environment.ExitCode = 60;
                return;
            }

            if (!string.Equals(
                    p15cGuiManualHardwareTestToken,
                    Hp8C40P15CGuiManualQualificationGate.RequiredToken,
                    StringComparison.Ordinal))
            {
                AppLog.Write(
                    $"HP 8C40 P15C real-GUI Manual qualification refused: explicit --8c40-p15c-test-token {Hp8C40P15CGuiManualQualificationGate.RequiredToken} is required.");
                Environment.ExitCode = 60;
                return;
            }

            if (string.IsNullOrWhiteSpace(p15cGuiManualMarkerRoot))
            {
                AppLog.Write(
                    "HP 8C40 P15C real-GUI Manual qualification requires --8c40-p15c-marker-root for isolated evidence.");
                Environment.ExitCode = 60;
                return;
            }

            if (!Hp8C40P15CGuiManualQualificationGate.NormalUserExecutionGatesClosed())
            {
                AppLog.Write(
                    "HP 8C40 P15C requires the normal post-M9 user Manual/Automatic gates to remain closed.");
                Environment.ExitCode = 60;
                return;
            }
        }
        else if (p15cGuiManualHardwareTestToken is not null ||
                 p15cGuiManualMarkerRoot is not null)
        {
            AppLog.Write(
                "Startup refused: P15C token/marker options are valid only with --8c40-p15c-gui-manual-test.");
            Environment.ExitCode = 60;
            return;
        }

        if (p15cGuiManualMarkerRoot is not null)
        {
            p15cGuiManualMarkerRoot =
                Path.GetFullPath(p15cGuiManualMarkerRoot);
        }

        if (p15d1TrayExitHardwareTest)
        {
            if (!Hp8C40P15D1TrayExitQualificationGate.PhysicalExecutionAuthorized)
            {
                AppLog.Write(
                    "HP 8C40 P15D1 tray-exit qualification refused: dedicated physical gate remains closed.");
                Environment.ExitCode = 60;
                return;
            }

            if (!string.Equals(
                    p15d1TrayExitHardwareTestToken,
                    Hp8C40P15D1TrayExitQualificationGate.RequiredToken,
                    StringComparison.Ordinal))
            {
                AppLog.Write(
                    $"HP 8C40 P15D1 tray-exit qualification refused: explicit --8c40-p15d1-test-token {Hp8C40P15D1TrayExitQualificationGate.RequiredToken} is required.");
                Environment.ExitCode = 60;
                return;
            }

            if (string.IsNullOrWhiteSpace(p15d1TrayExitMarkerRoot))
            {
                AppLog.Write(
                    "HP 8C40 P15D1 tray-exit qualification requires --8c40-p15d1-marker-root for isolated evidence.");
                Environment.ExitCode = 60;
                return;
            }

            if (!Hp8C40P15D1TrayExitQualificationGate.NormalUserExecutionGatesClosed())
            {
                AppLog.Write(
                    "HP 8C40 P15D1 requires the normal post-M9 user Manual/Automatic gates to remain closed.");
                Environment.ExitCode = 60;
                return;
            }
        }
        else if (p15d1TrayExitHardwareTestToken is not null ||
                 p15d1TrayExitMarkerRoot is not null)
        {
            AppLog.Write(
                "Startup refused: P15D1 token/marker options are valid only with --8c40-p15d1-tray-exit-test.");
            Environment.ExitCode = 60;
            return;
        }

        if (p15d1TrayExitMarkerRoot is not null)
        {
            p15d1TrayExitMarkerRoot =
                Path.GetFullPath(p15d1TrayExitMarkerRoot);
        }

        if (p15d2VariableManualHardwareTest)
        {
            if (!Hp8C40P15D2VariableManualQualificationGate.PhysicalExecutionAuthorized)
            {
                AppLog.Write(
                    "HP 8C40 P15D2 variable-Manual qualification refused: dedicated physical gate remains closed.");
                Environment.ExitCode = 60;
                return;
            }

            if (!string.Equals(
                    p15d2VariableManualHardwareTestToken,
                    Hp8C40P15D2VariableManualQualificationGate.RequiredToken,
                    StringComparison.Ordinal))
            {
                AppLog.Write(
                    $"HP 8C40 P15D2 variable-Manual qualification refused: explicit --8c40-p15d2-test-token {Hp8C40P15D2VariableManualQualificationGate.RequiredToken} is required.");
                Environment.ExitCode = 60;
                return;
            }

            if (string.IsNullOrWhiteSpace(p15d2VariableManualMarkerRoot))
            {
                AppLog.Write(
                    "HP 8C40 P15D2 variable-Manual qualification requires --8c40-p15d2-marker-root for isolated evidence.");
                Environment.ExitCode = 60;
                return;
            }

            if (!Hp8C40P15D2VariableManualQualificationGate.NormalUserExecutionGatesClosed())
            {
                AppLog.Write(
                    "HP 8C40 P15D2 requires the normal post-M9 user Manual/Automatic gates to remain closed.");
                Environment.ExitCode = 60;
                return;
            }
        }
        else if (p15d2VariableManualHardwareTestToken is not null ||
                 p15d2VariableManualMarkerRoot is not null)
        {
            AppLog.Write(
                "Startup refused: P15D2 token/marker options are valid only with --8c40-p15d2-variable-manual-test.");
            Environment.ExitCode = 60;
            return;
        }

        if (p15d2VariableManualMarkerRoot is not null)
        {
            p15d2VariableManualMarkerRoot =
                Path.GetFullPath(p15d2VariableManualMarkerRoot);
        }

        var modulesDirectory = ResolveModulesDirectory(args);
        if (modulesDirectory is null)
        {
            AppLog.Write("Startup failed: required PawnIO modules were not found.");
            MessageBox.Show(
                "PawnIO modules were not found. Run scripts/setup-pawnio-modules.ps1 from the repository first.",
                "VictusFanControl - modules not found",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var legacy88F8HardwareHarnessRequested =
            suspendHardwareTest ||
            gateDHardwareTest ||
            gateEHardwareTest ||
            gateF1HardwareTest ||
            gateF2HardwareTest ||
            gateG1HardwareTest ||
            gateG2HardwareTest;

        if (legacy88F8HardwareHarnessRequested)
        {
            var hardware = HardwareIdentityReader.ReadCurrent();

            if (!Hp88F8TargetProfile.Matches(
                    hardware,
                    out var legacyTargetReason))
            {
                AppLog.Write(
                    "Legacy 88F8 hardware harness startup refused before MainForm/backend creation: " +
                    legacyTargetReason);

                MessageBox.Show(
                    "This hardware-test mode belongs to the historical HP 88F8 / Legacy S3 target and is blocked on this computer. " +
                    "Use the dedicated 8C40 M-series qualification gates instead.",
                    "VictusFanControl - legacy harness blocked",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                Environment.ExitCode = 60;
                return;
            }
        }

        if (m6ModernStandbyHardwareTest ||
            m7HibernationHardwareTest ||
            m9dProductionLifecycleHardwareTest ||
            p15cGuiManualHardwareTest ||
            p15d1TrayExitHardwareTest ||
            p15d2VariableManualHardwareTest ||
            automaticFinalQualificationHardwareTest || block1Test)
        {
            var hardware = HardwareIdentityReader.ReadCurrent();

            if (!Hp8C40TargetProfile.Matches(
                    hardware,
                    out var lifecycleTargetReason))
            {
                var gateLabel =
                    automaticFinalQualificationHardwareTest
                        ? "final Automatic qualification"
                        : p15d2VariableManualHardwareTest
                            ? "P15D2 real-GUI variable Manual"
                            : p15d1TrayExitHardwareTest
                                ? "P15D1 real-GUI tray Exit"
                                : p15cGuiManualHardwareTest
                                    ? "P15C real-GUI Manual"
                                : m7HibernationHardwareTest
                                ? "M7 hibernation"
                                : m9dProductionLifecycleHardwareTest
                                    ? "M9D production-path Modern Standby"
                                    : "M6 Modern Standby";

                AppLog.Write(
                    $"HP 8C40 {gateLabel} startup refused before MainForm/backend creation: " +
                    lifecycleTargetReason);

                MessageBox.Show(
                    $"This {gateLabel} lifecycle qualification mode is restricted to the exact HP 8C40 / 9D0R1LA / BIOS F.18 target.",
                    $"VictusFanControl - {gateLabel} target blocked",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                Environment.ExitCode = 60;
                return;
            }
        }

        AppLog.Write($"Starting GUI. Modules={modulesDirectory}");

        if (hardwareTestModeCount == 0)
        {
            using var product = new ProductForm(modulesDirectory,args.Contains("--start-minimized"));
            Application.Run(product); AppLog.Write("Product GUI exited."); return;
        }

        using var form = new MainForm(
            modulesDirectory,
            suspendHardwareTest,
            gateDHardwareTest,
            gateEHardwareTest,
            gateF1HardwareTest,
            gateF2HardwareTest,
            gateG1HardwareTest,
            gateG2HardwareTest,
            m6ModernStandbyHardwareTest,
            m7HibernationHardwareTest,
            m9dProductionLifecycleHardwareTest,
            m9dProductionLifecycleMarkerRoot,
            p15cGuiManualHardwareTest,
            p15cGuiManualMarkerRoot,
            p15d1TrayExitHardwareTest,
            p15d1TrayExitMarkerRoot,
            p15d2VariableManualHardwareTest,
            p15d2VariableManualMarkerRoot,
            automaticFinalQualificationHardwareTest,
            automaticFinalQualificationMarkerRoot,
            block1Root);
        form.AutomaticPerformanceLimitsRequired = args.Contains("--automatic-performance-limits");
        if (args.Contains("--start-minimized")) form.Shown += (_, _) => form.StartInTray();
        Application.Run(form);

        AppLog.Write("GUI exited.");
    }

    private static string? ReadOptionValue(string[] args, string option)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static string? ResolveModulesDirectory(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--modules-dir", StringComparison.OrdinalIgnoreCase))
            {
                var explicitPath = Path.GetFullPath(args[i + 1]);
                return HasRequiredModules(explicitPath) ? explicitPath : null;
            }
        }

        var candidates = new List<string>
        {
            Path.Combine(Environment.CurrentDirectory, "modules"),
            Path.Combine(AppContext.BaseDirectory, "modules")
        };

        AddParentCandidates(candidates, Environment.CurrentDirectory);
        AddParentCandidates(candidates, AppContext.BaseDirectory);

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(HasRequiredModules);
    }

    private static void AddParentCandidates(List<string> candidates, string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        for (var depth = 0; directory is not null && depth < 6; depth++, directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, "modules"));
        }
    }

    private static bool HasRequiredModules(string path) =>
        File.Exists(Path.Combine(path, "IntelMSR.bin")) &&
        File.Exists(Path.Combine(path, "LpcACPIEC.bin"));
}
