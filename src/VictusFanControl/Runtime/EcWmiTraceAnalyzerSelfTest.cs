using System.Text.Json;
using VictusFanControl.Cli;

namespace VictusFanControl.Runtime;

internal static class EcWmiTraceAnalyzerSelfTest
{
    internal static int Run(TextWriter output)
    {
        try
        {
            static string Row(long op, string stage, long qpc, string detail = "", string utc = "2026-10-03T11:00:00Z") =>
                JsonSerializer.Serialize(new { Operation = op, Stage = stage, Qpc = qpc, Utc = utc, Detail = detail });
            var header = JsonSerializer.Serialize(new { kind = "session", pid = 42, qpcFrequency = 1000, coreMvid = "fixture" });
            var lines = new[] { header,
                Row(1, "wmi.send.begin", 100, "command=0x20008;type=0x2D;output=128"),
                Row(1, "wmi.parameters.begin", 200), Row(1, "wmi.parameters.end", 250), Row(1, "wmi.invoke.begin", 500),
                Row(2, "wmi.send.begin", 1000, "command=0x20008;type=0x2E;output=0"),
                Row(2, "wmi.parameters.begin", 1010), Row(2, "wmi.parameters.end", 1020), Row(2, "wmi.invoke.begin", 1030),
                Row(2, "wmi.invoke.end", 1070), Row(2, "wmi.response", 1075, "rc=0;bytes=0"), Row(2, "wmi.send.end", 1080),
                Row(1, "wmi.invoke.end", 5500), Row(1, "wmi.response", 5510, "rc=0;bytes=128"),
                Row(1, "wmi.send.end", 5600, utc: "2026-10-03T10:59:00Z"), // Deliberate wall-clock regression.
                Row(3, "ec.read.begin", 10_000, "register=0xEC"), Row(3, "ec.read.end", 10_235, "register=0xEC;value=0x90"),
                Row(0, "isolation.finished", 10_300, "deniedEc=0;deniedWmi=0") };
            var valid = EcWmiTraceAnalyzer.Analyze(new StringReader(string.Join('\n', lines)));
            Check(valid.ChronologyConsistent && valid.NativeWmi.Count == 2 && valid.NativeWmi.MedianMs == 2520 &&
                valid.NativeWmi.P95Ms == 5000 && valid.NativeWmiAtLeastOneSecond == 1, "QPC pairing/statistics lost");
            var read = valid.WmiCalls.Single(c => c.Operation == 1);
            Check(read.NativeMs == 5000 && read.TotalMs == 5500 && read.ParametersMs == 50 && read.OutsideInvokeMs == 500 &&
                read.Classification == "rpm-read" && valid.WmiCalls.Single(c => c.Operation == 2).Classification == "fan-write-or-release", "native/preparation/total separation lost");
            Check(valid.EcReads.Single().DurationMs == 235 && valid.EcReads.Single().DecodedMaxFanBit == false, "EC raw 90h mislabeled");

            var incomplete = EcWmiTraceAnalyzer.Analyze(new StringReader(header + "\n" + Row(7, "wmi.send.begin", 100)));
            Check(!incomplete.ChronologyConsistent && !incomplete.WmiCalls.Single().Complete && incomplete.WmiCalls.Single().TotalMs is null, "pending call fabricated a duration");
            var loss = EcWmiTraceAnalyzer.Analyze(new StringReader(string.Join('\n', lines) + "\n{broken\n" +
                "{\"kind\":\"records-dropped\",\"total\":9}\n{\"kind\":\"records-dropped\",\"total\":7}\n{\"kind\":\"capture-limit-reached\"}\n" +
                Row(1, "wmi.send.begin", 11000) + "\n" + Row(999, "wmi.send.end", 11001)));
            Check(!loss.ChronologyConsistent && loss.InvalidLines == 1 && loss.DroppedRecords == 9 && loss.CaptureLimitReached &&
                loss.DuplicateBegins == 1 && loss.OrphanEnds == 1, "capture holes hidden");
            var tail = EcWmiTraceAnalyzer.Analyze(new StringReader(string.Join('\n', lines.Skip(1))));
            Check(!tail.HasSessionHeader && !tail.ChronologyConsistent && tail.NativeWmi.Count == 0, "headerless tail assumed a clock");
            var wrongClock = EcWmiTraceAnalyzer.Analyze(new StringReader(string.Join('\n', lines).Replace("\"Qpc\":5500", "\"Qpc\":50")));
            Check(!wrongClock.ChronologyConsistent && wrongClock.WmiCalls.Single(c => c.Operation == 1).NativeMs is null, "negative native interval accepted");
            var failed = EcWmiTraceAnalyzer.Analyze(new StringReader(string.Join('\n', new[] { header,
                Row(8, "wmi.send.begin", 100, "command=0x20008;type=0x2D;output=128"),
                Row(8, "wmi.parameters.begin", 110), Row(8, "wmi.parameters.end", 120), Row(8, "wmi.invoke.begin", 130),
                Row(8, "wmi.failure", 5000, "synthetic native exception"), Row(8, "wmi.send.end", 5001) })));
            Check(failed.WmiCalls.Single().Failed && failed.WmiCalls.Single().NativeMs is null && failed.WmiCalls.Single().TotalMs == 4901,
                "native exception inferred an exact return timestamp");
            try { EcWmiTraceAnalyzer.Analyze(new StringReader(header + "\n" + header)); throw new Exception("Concatenated session accepted."); }
            catch (InvalidDataException) { }
            var typed = EcWmiTraceAnalyzer.Analyze(new StringReader(header + "\n" + "{\"Stage\":\"wmi.send.begin\",\"Qpc\":\"bad\",\"Operation\":1}"));
            Check(!typed.ChronologyConsistent, "invalid field types accepted");

            var options = CliOptions.Parse(["--analyze-ec-wmi-trace", "fixture.log", "--analysis-output-dir", "out"]);
            Check(options.AnalyzeEcWmiTracePath is not null && options.TraceAnalysisOutputDirectory is not null, "offline options lost");
            foreach (var args in new[] { new[] { "--analyze-ec-wmi-trace", "fixture.log", "--restore-hp-auto" },
                new[] { "--analysis-output-dir", "out" }, new[] { "--wmi-only-investigation", "--analyze-ec-wmi-trace", "fixture.log" } })
            {
                try { CliOptions.Parse(args); throw new Exception("Offline analysis accepted mixed control flags."); }
                catch (ArgumentException) { }
            }
            output.WriteLine("PASS: offline EC/WMI QPC pairs, native/total separation, interleaving, wall-clock jump, pending calls, losses, raw MaxFan decoding and command isolation.");
            return 0;
        }
        catch (Exception ex) { output.WriteLine("FAIL: offline EC/WMI analyzer: " + ex); return 1; }
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
