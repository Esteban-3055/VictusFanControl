using System.Globalization;
using System.Text;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Control.Adaptive;

/// <summary>
/// Offline replay of existing VictusFanControl telemetry CSV through the
/// adaptive shadow evaluator. This type performs file I/O only; it never
/// constructs hardware/control backends and cannot write fan state.
/// </summary>
public static class AdaptiveFanPolicyShadowReplay
{
    private static readonly string[] RequiredColumns =
    [
        "timestamp_utc",
        "cpu_name",
        "cpu_package_temp_c",
        "cpu_core_temps_c",
        "cpu_package_power_w",
        "cpu_load_pct",
        "gpu_name",
        "gpu_temp_c",
        "gpu_power_w",
        "gpu_load_pct",
        "cpu_fan_rpm",
        "gpu_fan_rpm"
    ];

    public static async Task<int> RunAsync(
        string configPath,
        string inputPath,
        string outputPath,
        TextWriter console,
        CancellationToken cancellationToken)
    {
        try
        {
            var fullInput = Path.GetFullPath(inputPath);
            var fullOutput = Path.GetFullPath(outputPath);

            if (string.Equals(
                    fullInput,
                    fullOutput,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Adaptive shadow replay input and output paths must differ.");
            }

            var config =
                AdaptiveFanPolicyShadowConfig.Load(
                    configPath);

            var evaluator =
                new AdaptiveFanPolicyShadowEvaluator(
                    BuildValidatedTargetIdentity(),
                    config);

            var outputDirectory =
                Path.GetDirectoryName(
                    fullOutput);

            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(
                    outputDirectory);
            }

            using var reader =
                new StreamReader(
                    fullInput,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);

            await using var writer =
                new StreamWriter(
                    fullOutput,
                    append: false,
                    new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false));

            var headerLine =
                await reader.ReadLineAsync(
                    cancellationToken)
                ?? throw new InvalidDataException(
                    "Adaptive shadow replay input CSV is empty.");

            var header =
                BuildHeaderIndex(
                    headerLine);

            await writer.WriteLineAsync(
                "timestamp_utc,safety_ready,thermal_emergency,policy_accepted," +
                "raw_demand_level,recommended_equal_level,intent,detail,safety_reasons");

            var lineNumber = 1;
            var processed = 0;
            var policyAccepted = 0;
            var safetyRejected = 0;
            var releaseIntents = 0;
            var changedLevelIntents = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var line =
                    await reader.ReadLineAsync(
                        cancellationToken);

                if (line is null)
                {
                    break;
                }

                lineNumber++;

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                TelemetrySnapshot snapshot;
                try
                {
                    snapshot =
                        ParseSnapshot(
                            header,
                            line);
                }
                catch (Exception ex)
                    when (ex is FormatException or InvalidDataException)
                {
                    throw new InvalidDataException(
                        $"Adaptive shadow replay invalid CSV row at line {lineNumber}: " +
                        ex.Message,
                        ex);
                }

                var evaluation =
                    evaluator.Evaluate(
                        SystemState.Healthy,
                        snapshot,
                        snapshot.Timestamp);

                processed++;

                if (evaluation.PolicyAccepted)
                {
                    policyAccepted++;
                }

                if (!evaluation.SafetyPreconditionsReady)
                {
                    safetyRejected++;
                }

                if (evaluation.Intent.Kind ==
                    AdaptiveFanControlIntentKind.ReleaseToFirmware)
                {
                    releaseIntents++;
                }

                if (evaluation.Intent.Kind is
                    AdaptiveFanControlIntentKind.EnterCustomAndApply or
                    AdaptiveFanControlIntentKind.ApplyChangedLevel)
                {
                    changedLevelIntents++;
                }

                await writer.WriteLineAsync(
                    string.Join(
                        ',',
                        Escape(
                            snapshot.Timestamp.ToString(
                                "O",
                                CultureInfo.InvariantCulture)),
                        evaluation.SafetyPreconditionsReady
                            ? "true"
                            : "false",
                        evaluation.EffectiveThermalEmergency
                            ? "true"
                            : "false",
                        evaluation.PolicyAccepted
                            ? "true"
                            : "false",
                        Number(
                            evaluation.RawDemandLevel),
                        evaluation.RecommendedEqualLevel?
                            .ToString(
                                CultureInfo.InvariantCulture)
                            ?? string.Empty,
                        evaluation.Intent.Kind.ToString(),
                        Escape(
                            evaluation.Detail),
                        Escape(
                            string.Join(
                                " | ",
                                evaluation.SafetyReasons))));
            }

            await writer.FlushAsync(
                cancellationToken);

            if (processed == 0)
            {
                throw new InvalidDataException(
                    "Adaptive shadow replay input contains no telemetry rows.");
            }

            console.WriteLine(
                "Adaptive policy shadow replay: PASS");
            console.WriteLine(
                $"  target                : {Hp8C40TargetProfile.Instance.Id}");
            console.WriteLine(
                $"  processed rows        : {processed}");
            console.WriteLine(
                $"  policy accepted       : {policyAccepted}");
            console.WriteLine(
                $"  safety rejected       : {safetyRejected}");
            console.WriteLine(
                $"  command-change intents: {changedLevelIntents}");
            console.WriteLine(
                $"  release intents       : {releaseIntents}");
            console.WriteLine(
                $"  output                : {fullOutput}");
            console.WriteLine(
                "  hardware writes       : 0 (offline shadow replay only)");

            return 0;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            console.WriteLine(
                $"Adaptive policy shadow replay: FAIL_CLOSED: " +
                $"{ex.GetType().Name}: {ex.Message}");

            return 32;
        }
    }

    internal static string[] ParseCsvLine(
        string line)
    {
        var fields =
            new List<string>();

        var current =
            new StringBuilder();

        var quoted = false;

        for (var index = 0;
             index < line.Length;
             index++)
        {
            var character =
                line[index];

            if (character == '"')
            {
                if (quoted &&
                    index + 1 < line.Length &&
                    line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                    continue;
                }

                quoted = !quoted;
                continue;
            }

            if (character == ',' &&
                !quoted)
            {
                fields.Add(
                    current.ToString());

                current.Clear();
                continue;
            }

            current.Append(
                character);
        }

        if (quoted)
        {
            throw new InvalidDataException(
                "CSV row contains an unterminated quoted field.");
        }

        fields.Add(
            current.ToString());

        return fields.ToArray();
    }

    internal static TelemetrySnapshot ParseSnapshotForSelfTest(
        string headerLine,
        string dataLine) =>
        ParseSnapshot(
            BuildHeaderIndex(
                headerLine),
            dataLine);

    private static Dictionary<string, int> BuildHeaderIndex(
        string headerLine)
    {
        var columns =
            ParseCsvLine(
                headerLine);

        var header =
            new Dictionary<string, int>(
                StringComparer.Ordinal);

        for (var index = 0;
             index < columns.Length;
             index++)
        {
            if (!header.TryAdd(
                    columns[index],
                    index))
            {
                throw new InvalidDataException(
                    $"Telemetry CSV contains duplicate column '{columns[index]}'.");
            }
        }

        foreach (var required in RequiredColumns)
        {
            if (!header.ContainsKey(required))
            {
                throw new InvalidDataException(
                    $"Telemetry CSV is missing required column '{required}'.");
            }
        }

        return header;
    }

    private static TelemetrySnapshot ParseSnapshot(
        IReadOnlyDictionary<string, int> header,
        string line)
    {
        var fields =
            ParseCsvLine(
                line);

        string Field(
            string name)
        {
            var index =
                header[name];

            if (index >= fields.Length)
            {
                throw new InvalidDataException(
                    $"Telemetry CSV row has no value for '{name}'.");
            }

            return fields[index];
        }

        var timestamp =
            DateTimeOffset.Parse(
                Field("timestamp_utc"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);

        var snapshot =
            new TelemetrySnapshot(
                Timestamp: timestamp,
                CpuName:
                    EmptyToNull(
                        Field("cpu_name")),
                CpuTemperatureC:
                    OptionalDouble(
                        Field("cpu_package_temp_c")),
                CpuPackagePowerW:
                    OptionalDouble(
                        Field("cpu_package_power_w")),
                CpuLoadPercent:
                    OptionalDouble(
                        Field("cpu_load_pct")),
                GpuName:
                    EmptyToNull(
                        Field("gpu_name")),
                GpuTemperatureC:
                    OptionalDouble(
                        Field("gpu_temp_c")),
                GpuPowerW:
                    OptionalDouble(
                        Field("gpu_power_w")),
                GpuLoadPercent:
                    OptionalDouble(
                        Field("gpu_load_pct")),
                CpuFanRpm:
                    OptionalDouble(
                        Field("cpu_fan_rpm")),
                GpuFanRpm:
                    OptionalDouble(
                        Field("gpu_fan_rpm")))
            {
                CpuCoreTemperatures =
                    ParseCoreTemperatures(
                        Field("cpu_core_temps_c")),
                CpuExpectedPhysicalCoreCount =
                    Hp8C40TargetProfile.Instance
                        .ExpectedPhysicalCoreCount
            };

        return snapshot;
    }

    private static IReadOnlyList<CpuCoreTemperatureSample>
        ParseCoreTemperatures(
            string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<CpuCoreTemperatureSample>();
        }

        var result =
            new List<CpuCoreTemperatureSample>();

        foreach (var item in value.Split(
                     '|',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var parts =
                item.Split(
                    ':',
                    3,
                    StringSplitOptions.None);

            if (parts.Length != 3 ||
                parts[0].Length < 2 ||
                parts[0][0] != 'C' ||
                !int.TryParse(
                    parts[0][1..],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var coreIndex) ||
                !double.TryParse(
                    parts[2],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var temperature))
            {
                throw new FormatException(
                    $"Invalid cpu_core_temps_c item '{item}'.");
            }

            result.Add(
                new CpuCoreTemperatureSample(
                    CoreIndex: coreIndex,
                    LogicalProcessorIndex: coreIndex,
                    CoreType: parts[1],
                    TemperatureC: temperature));
        }

        return result;
    }

    private static double? OptionalDouble(
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return double.Parse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture);
    }

    private static string? EmptyToNull(
        string value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value;

    private static string Number(
        double? value) =>
        value?.ToString(
            "0.###",
            CultureInfo.InvariantCulture)
        ?? string.Empty;

    private static string Escape(
        string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var escaped =
            value.Replace(
                """,
                """");

        return escaped.IndexOfAny(
                   [',', '"', '\r', '\n']) >= 0
            ? $""{escaped}""
            : escaped;
    }

    private static HardwareIdentity BuildValidatedTargetIdentity() =>
        new(
            Hp8C40TargetProfile.BoardManufacturer,
            Hp8C40TargetProfile.BoardProduct,
            Hp8C40TargetProfile.BoardVersion,
            Hp8C40TargetProfile.SystemManufacturer,
            Hp8C40TargetProfile.SystemProductName,
            $"{Hp8C40TargetProfile.SystemSkuPrefix}#AKH",
            Hp8C40TargetProfile.ValidatedBiosVersion);
}
