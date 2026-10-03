using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Runtime;

internal sealed record TraceMetric(int Count, double? MedianMs, double? P95Ms, double? MaximumMs);
internal sealed record TraceWmiCall(long Operation, string? StartedUtc, string? EndedUtc, string Command,
    string CommandType, string Classification, double? TotalMs, double? NativeMs, double? ParametersMs,
    double? OutsideInvokeMs, bool Complete, bool Failed, int? ReturnCode, string? Failure);
internal sealed record TraceEcRead(long Operation, string? StartedUtc, string Register, string Result,
    string? Value, double? DurationMs, bool? DecodedMaxFanBit, string? Failure);
internal sealed record TraceAnalysis(int Schema, int? Pid, string? CoreMvid, long? QpcFrequency,
    int ParsedLines, int InvalidLines, int DuplicateBegins, int OrphanEnds, long DroppedRecords,
    bool CaptureLimitReached, bool HasSessionHeader, bool HasIsolationFinished, bool ChronologyConsistent,
    TraceMetric NativeWmi, TraceMetric TotalWmi, TraceMetric OutsideInvoke,
    int NativeWmiAtLeastOneSecond, IReadOnlyList<TraceWmiCall> WmiCalls, IReadOnlyList<TraceEcRead> EcReads,
    IReadOnlyList<string> Warnings);

/// <summary>Offline JSONL analysis. No hardware APIs, WMI, EC or driver loading.</summary>
internal static class EcWmiTraceAnalyzer
{
    private sealed class WmiPending(long operation, long qpc, string? utc, string detail)
    {
        internal long Operation = operation, Start = qpc;
        internal string? Utc = utc;
        internal string Detail = detail;
        internal long? InvokeStart, InvokeEnd, ParametersStart, ParametersEnd;
        internal bool Failed;
        internal int? ReturnCode;
        internal string? Failure;
    }
    private sealed record EcPending(long Start, string? Utc, string Register);

    internal static TraceAnalysis Analyze(TextReader reader)
    {
        var pendingWmi = new Dictionary<long, WmiPending>();
        var pendingEc = new Dictionary<long, EcPending>();
        var completedWmi = new HashSet<long>();
        var completedEc = new HashSet<long>();
        var calls = new List<TraceWmiCall>();
        var ec = new List<TraceEcRead>();
        int parsed = 0, invalid = 0, duplicates = 0, orphans = 0, headers = 0;
        int? pid = null;
        long? frequency = null;
        string? mvid = null;
        long dropped = 0;
        var limit = false;
        var finished = false;
        var invalidClock = false;
        var warnings = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (parsed + invalid >= 500_000) throw new InvalidDataException("Trace exceeds the 500,000-line analysis limit.");
            if (line.Length > 65_536) { invalid++; continue; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var row = document.RootElement;
                if (row.ValueKind != JsonValueKind.Object) { invalid++; continue; }
                var kind = String(row, "kind");
                if (kind == "session")
                {
                    headers++;
                    // Never pair IDs from concatenated processes or clock domains.
                    if (headers != 1 || parsed != 0) throw new InvalidDataException("Session header must be first and unique; analyze each process log separately.");
                    var rawPid = Integer(row, "pid");
                    if (rawPid is > 0 and <= int.MaxValue) pid = (int)rawPid.Value;
                    frequency = Integer(row, "qpcFrequency");
                    if (frequency is null or <= 0) { invalidClock = true; frequency = null; }
                    mvid = String(row, "coreMvid");
                    parsed++;
                    continue;
                }
                if (kind == "records-dropped") { dropped = Math.Max(dropped, Integer(row, "total") ?? 1); parsed++; continue; }
                if (kind == "capture-limit-reached") { limit = true; parsed++; continue; }
                var stage = String(row, "Stage");
                if (stage is null) { invalid++; continue; }
                var operation = Integer(row, "Operation");
                var qpc = Integer(row, "Qpc");
                var utc = String(row, "Utc");
                var detail = String(row, "Detail") ?? "";
                parsed++;
                if (stage == "isolation.finished") finished = true;
                if (operation is null or < 0 || qpc is null or < 0) { invalidClock = true; continue; }
                var id = operation.Value;
                var tick = qpc.Value;

                if (stage == "wmi.send.begin")
                {
                    if (id == 0 || completedWmi.Contains(id) || !pendingWmi.TryAdd(id, new(id, tick, utc, detail))) duplicates++;
                }
                else if (stage == "wmi.send.end")
                {
                    if (!pendingWmi.Remove(id, out var call)) { orphans++; continue; }
                    completedWmi.Add(id);
                    var total = Milliseconds(call.Start, tick, frequency);
                    var native = Milliseconds(call.InvokeStart, call.InvokeEnd, frequency);
                    var parameters = Milliseconds(call.ParametersStart, call.ParametersEnd, frequency);
                    if (total is null && frequency.HasValue || native > total || parameters > total) invalidClock = true;
                    if (!ValidPhase(call.ParametersStart, call.ParametersEnd, call.Start, tick, call.Failed))
                    { invalidClock = true; parameters = null; }
                    if (!ValidPhase(call.InvokeStart, call.InvokeEnd, call.Start, tick, call.Failed))
                    { invalidClock = true; native = null; }
                    calls.Add(Finish(call, utc, total, native, parameters, total.HasValue));
                }
                else if (stage.StartsWith("wmi.", StringComparison.Ordinal) && pendingWmi.TryGetValue(id, out var call))
                {
                    switch (stage)
                    {
                        case "wmi.invoke.begin": call.InvokeStart = tick; break;
                        case "wmi.invoke.end": call.InvokeEnd = tick; break;
                        case "wmi.parameters.begin": call.ParametersStart = tick; break;
                        case "wmi.parameters.end": call.ParametersEnd = tick; break;
                        case "wmi.failure": call.Failed = true; call.Failure = detail; break;
                        case "wmi.response":
                            if (int.TryParse(Field(detail, "rc"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rc))
                            { call.ReturnCode = rc; call.Failed |= rc != 0; }
                            break;
                    }
                }
                else if (stage == "ec.read.begin")
                {
                    if (id == 0 || completedEc.Contains(id) || !pendingEc.TryAdd(id, new(tick, utc, Field(detail, "register")))) duplicates++;
                }
                else if (stage is "ec.read.end" or "ec.read.failure")
                {
                    if (!pendingEc.Remove(id, out var read)) { orphans++; continue; }
                    completedEc.Add(id);
                    var duration = Milliseconds(read.Start, tick, frequency);
                    if (duration is null && frequency.HasValue) invalidClock = true;
                    var value = stage == "ec.read.end" ? Field(detail, "value") : null;
                    bool? bit = null;
                    if (read.Register.Equals("0xEC", StringComparison.OrdinalIgnoreCase) && TryByte(value, out var raw))
                        bit = Hp8C40MaxFanFlags.DecodeMaxFanBit(raw);
                    ec.Add(new(id, read.Utc, read.Register, stage == "ec.read.end" ? "protocol-success" : "protocol-failure",
                        value, duration, bit, stage == "ec.read.failure" ? detail : null));
                }
            }
            catch (JsonException) { invalid++; }
        }
        foreach (var call in pendingWmi.Values) calls.Add(Finish(call, null, null,
            Milliseconds(call.InvokeStart, call.InvokeEnd, frequency),
            Milliseconds(call.ParametersStart, call.ParametersEnd, frequency), false));
        foreach (var (id, read) in pendingEc) ec.Add(new(id, read.Utc, read.Register, "incomplete", null, null, null, null));
        if (headers == 0) warnings.Add("No session header: PID, clock frequency and durations are unavailable. A bounded tail may have removed the header.");
        if (headers > 0 && !pid.HasValue) warnings.Add("Session PID is missing or invalid.");
        if (invalidClock) warnings.Add("Missing or inconsistent QPC clock data; affected intervals cannot be used as timing evidence.");
        if (invalid > 0) warnings.Add($"Invalid or oversized JSONL records: {invalid}.");
        if (duplicates > 0 || orphans > 0) warnings.Add($"Duplicate begins={duplicates}; unmatched ends={orphans}.");
        if (pendingWmi.Count > 0 || pendingEc.Count > 0) warnings.Add($"Unfinished operations: WMI={pendingWmi.Count}; EC={pendingEc.Count}. No elapsed time is invented for missing ends.");
        if (dropped > 0 || limit) warnings.Add($"Explicit capture loss: dropped records={dropped}; file limit={limit}.");
        warnings.Add("EC protocol success does not establish register/value integrity. Bit decoding is an interpretation of the observed byte.");
        warnings.Add("This report analyzes one application log. It does not decode ETL/EVTX, prove an AC transition or establish physical causality.");
        var consistent = headers == 1 && pid.HasValue && !invalidClock && invalid == 0 && duplicates == 0 && orphans == 0 &&
            pendingWmi.Count == 0 && pendingEc.Count == 0 && dropped == 0 && !limit;
        return new(1, pid, mvid, frequency, parsed, invalid, duplicates, orphans, dropped, limit, headers == 1,
            finished, consistent, Metric(calls.Select(c => c.NativeMs)), Metric(calls.Select(c => c.TotalMs)),
            Metric(calls.Select(c => c.OutsideInvokeMs)), calls.Count(c => c.NativeMs >= 1000), calls, ec, warnings);
    }

    private static TraceWmiCall Finish(WmiPending call, string? utc, double? total, double? native, double? parameters, bool complete)
    {
        var command = Field(call.Detail, "command");
        var type = Field(call.Detail, "type");
        var classification = command == "0x20008" ? type switch
        { "0x2D" => "rpm-read", "0x2E" => "fan-write-or-release", "0x6" => "legacy-default", _ => "other" } : "other";
        return new(call.Operation, call.Utc, utc, command, type, classification, total, native, parameters,
            total.HasValue && native.HasValue && total >= native ? total - native : null,
            complete, call.Failed, call.ReturnCode, call.Failure);
    }
    private static double? Milliseconds(long? begin, long? end, long? frequency) =>
        begin.HasValue && end.HasValue && frequency is > 0 && end >= begin
            ? (double)(((decimal)end.Value - begin.Value) * 1000 / frequency.Value) : null;
    private static bool ValidPhase(long? begin, long? end, long parentBegin, long parentEnd, bool failed) =>
        begin.HasValue && end.HasValue ? begin >= parentBegin && end >= begin && end <= parentEnd
        : failed && !end.HasValue && (!begin.HasValue || begin >= parentBegin && begin <= parentEnd);
    private static string? String(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? Integer(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    private static string Field(string detail, string name) => detail.Split(';')
        .FirstOrDefault(field => field.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..] ?? "";
    private static bool TryByte(string? text, out byte value) => byte.TryParse(text?.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
        NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    internal static TraceMetric Metric(IEnumerable<double?> samples)
    {
        var sorted = samples.Where(x => x.HasValue).Select(x => x!.Value).Order().ToArray();
        if (sorted.Length == 0) return new(0, null, null, null);
        var median = sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
        return new(sorted.Length, median, sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], sorted[^1]);
    }

    internal static int Run(string path, string outputDirectory, TextWriter output)
    {
        try
        {
            var fullSource = Path.GetFullPath(path);
            foreach (var name in new[] { "summary.json", "wmi-calls.csv", "ec-reads.csv", "report.md" })
                if (string.Equals(fullSource, Path.GetFullPath(Path.Combine(outputDirectory, name)), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Analysis output must not overwrite the source log.");
            // Snapshot a bounded source prefix. Hash exactly the bytes analyzed,
            // even if a live producer subsequently appends to the original file.
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (source.Length > 64 * 1024 * 1024) throw new InvalidDataException("Analysis input exceeds 64 MiB.");
            var bytes = new byte[checked((int)source.Length)];
            source.ReadExactly(bytes);
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true));
            var analysis = Analyze(reader);
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory, "summary.json"), JsonSerializer.Serialize(new
            { Source = Path.GetFullPath(path), SourceSnapshotBytes = bytes.Length, SourceSnapshotSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                AnalyzedUtc = DateTimeOffset.UtcNow, Analysis = analysis }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            using (var csv = new StreamWriter(Path.Combine(outputDirectory, "wmi-calls.csv"), false, new UTF8Encoding(false)))
            {
                csv.WriteLine("Operation,StartedUtc,EndedUtc,Command,CommandType,Classification,TotalMs,NativeMs,ParametersMs,OutsideInvokeMs,Complete,Failed,ReturnCode,Failure");
                foreach (var call in analysis.WmiCalls) csv.WriteLine(Csv(call.Operation, call.StartedUtc, call.EndedUtc, call.Command, call.CommandType,
                    call.Classification, call.TotalMs, call.NativeMs, call.ParametersMs, call.OutsideInvokeMs, call.Complete, call.Failed, call.ReturnCode, call.Failure));
            }
            using (var csv = new StreamWriter(Path.Combine(outputDirectory, "ec-reads.csv"), false, new UTF8Encoding(false)))
            {
                csv.WriteLine("Operation,StartedUtc,Register,Result,Value,DurationMs,DecodedMaxFanBit,Failure");
                foreach (var read in analysis.EcReads) csv.WriteLine(Csv(read.Operation, read.StartedUtc, read.Register, read.Result, read.Value, read.DurationMs, read.DecodedMaxFanBit, read.Failure));
            }
            var report = new StringBuilder("# Analisis offline EC/WMI\n\nTiempos QPC; horas UTC. No efectua lecturas de hardware.\n\n");
            report.AppendLine($"PID: {analysis.Pid?.ToString() ?? "desconocido"}. Cronologia consistente: {analysis.ChronologyConsistent}. Marcador isolation.finished: {analysis.HasIsolationFinished}.");
            report.AppendLine("\n| Medida | Intervalos | Mediana ms | P95 ms | Maximo ms |\n|---|---:|---:|---:|---:|");
            foreach (var (name, metric) in new[] { ("WMI nativo", analysis.NativeWmi), ("WMI total", analysis.TotalWmi), ("Fuera de invoke", analysis.OutsideInvoke) })
                report.AppendLine($"| {name} | {metric.Count} | {Number(metric.MedianMs)} | {Number(metric.P95Ms)} | {Number(metric.MaximumMs)} |");
            report.AppendLine($"\nConsultas nativas de al menos 1000 ms: {analysis.NativeWmiAtLeastOneSecond}. EC: {analysis.EcReads.Count} intentos observados. P95 usa rango mas cercano; mediana promedia los dos centrales cuando corresponde.");
            report.AppendLine("\n| Inicio UTC | Comando/tipo | Nativo ms | Total ms | Resultado |\n|---|---|---:|---:|---|");
            foreach (var call in analysis.WmiCalls.Where(c => c.NativeMs >= 1000).OrderByDescending(c => c.NativeMs).Take(20))
                report.AppendLine($"| {call.StartedUtc} | {call.Command}/{call.CommandType} | {Number(call.NativeMs)} | {Number(call.TotalMs)} | {(call.Failed ? "fallo registrado" : "sin fallo registrado")} |");
            report.AppendLine("\nObservaciones y limites:\n");
            foreach (var warning in analysis.Warnings) report.AppendLine("- " + warning);
            File.WriteAllText(Path.Combine(outputDirectory, "report.md"), report.ToString(), new UTF8Encoding(false));
            output.WriteLine($"Offline report: {Path.GetFullPath(outputDirectory)}; WMI={analysis.WmiCalls.Count}; native >=1s={analysis.NativeWmiAtLeastOneSecond}; chronology consistent={analysis.ChronologyConsistent}.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or DecoderFallbackException)
        { output.WriteLine("Trace analysis failed: " + ex.Message); return 3; }
    }
    private static string Number(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "N/D";
    private static string Csv(params object?[] values) => string.Join(',', values.Select(value =>
        "\"" + (value is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "").Replace("\"", "\"\"") + "\""));
}
