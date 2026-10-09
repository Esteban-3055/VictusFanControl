using VictusFanControl.Control;
using System.Management;
using VictusFanControl.Runtime;

namespace VictusFanControl.Hardware.Hp;

public readonly record struct HpBiosRequest(
    uint Command,
    uint CommandType,
    byte[] Payload,
    int OutputSize);

public readonly record struct HpBiosResponse(
    int ReturnCode,
    byte[] Data);

public sealed class HpBiosCallException : Exception
{
    public HpBiosCallException(string message) : base(message)
    {
    }

    public HpBiosCallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Minimal independent HP OMEN BIOS/WMI transport.
/// This reproduces the observable WMI contract only; it does not depend on
/// OmenMon binaries or source at runtime.
/// </summary>
public sealed class HpOmenBiosWmiClient
{
    private const string NamespacePath = @"\\.\root\wmi";
    private const string DataClassName = "hpqBDataIn";
    private const string MethodClassName = "hpqBIntM";
    private const string MethodInstanceName = @"ACPI\PNP0C14\0_0";
    private const string DataFieldName = "hpqBData";
    private const string ReturnCodeFieldName = "rwReturnCode";

    private static readonly byte[] Signature = [0x53, 0x45, 0x43, 0x55];
    private static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(5);

    private readonly ManagementScope _scope;
    private readonly ManagementPath _methodPath;

    public HpOmenBiosWmiClient()
    {
        EcWmiInvestigationTrace.Initialize();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("HP BIOS/WMI access requires Windows.");
        }

        // Establish the WMI connection before any fan write is attempted and
        // reuse it for readback and fail-safe restoration.
        _scope = new ManagementScope(NamespacePath);
        var discovery = EcWmiInvestigationTrace.Begin("wmi.connect.begin", NamespacePath);
        _scope.Connect();
        EcWmiInvestigationTrace.Record(discovery, "wmi.connect.end", NamespacePath);

        // Resolve the method instance before any write. Later readback/restore
        // calls do not need to rediscover the provider object.
        var lookup = EcWmiInvestigationTrace.Begin("wmi.discovery.begin", MethodClassName);
        using var method = FindMethodInstance(_scope);
        EcWmiInvestigationTrace.Record(lookup, "wmi.discovery.end", MethodClassName);
        var relativePath = method.Path?.RelativePath;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new HpBiosCallException("HP BIOS WMI method instance has no usable path.");
        }

        _methodPath = new ManagementPath(relativePath);
    }

    public int Send(HpBiosRequest request) =>
        SendWithResponse(request).ReturnCode;

    public HpBiosResponse SendWithResponse(HpBiosRequest request)
    {
        return WmiFanExperimentBoundary.Enabled
            ? WmiFanExperimentBoundary.Serialize(request, () => SendWithResponseCore(request))
            : SendWithResponseCore(request);
    }

    private HpBiosResponse SendWithResponseCore(HpBiosRequest request)
    {
        WmiOnlyInvestigationPolicy.EnsureWmiRequestAllowed(request);
        if (request.OutputSize is not (0 or 4 or 128 or 1024 or 4096))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Unsupported HP BIOS output size.");
        }

        var trace = EcWmiInvestigationTrace.Enabled
            ? EcWmiInvestigationTrace.Begin("wmi.send.begin",
                $"command=0x{request.Command:X};type=0x{request.CommandType:X};output={request.OutputSize}") : 0;
        try
        {
            using var dataClass = new ManagementClass(
                _scope,
                new ManagementPath(DataClassName),
                options: null);

            using var data = dataClass.CreateInstance()
                ?? throw new HpBiosCallException("Could not create hpqBDataIn.");

            data["Sign"] = Signature.ToArray();
            data["Command"] = request.Command;
            data["CommandType"] = request.CommandType;
            data["Size"] = (uint)request.Payload.Length;
            data[DataFieldName] = request.Payload.ToArray();

            using var target = new ManagementObject(
                _scope,
                _methodPath,
                options: null);
            var methodName = $"hpqBIOSInt{request.OutputSize}";

            EcWmiInvestigationTrace.Record(trace, "wmi.parameters.begin", methodName);
            using var methodInput = target.GetMethodParameters(methodName)
                ?? throw new HpBiosCallException(
                    $"HP WMI method {methodName} does not expose input parameters.");

            EcWmiInvestigationTrace.Record(trace, "wmi.parameters.end", methodName);
            methodInput["InData"] = data;

            var invokeOptions = new InvokeMethodOptions
            {
                Timeout = InvokeTimeout
            };

            EcWmiInvestigationTrace.Record(trace, "wmi.invoke.begin", methodName);
            if (WmiFanExperimentBoundary.Enabled) WmiFanExperimentBoundary.EnsureRequestAllowed(request);
            FanDispatchAdmissionScope.EnsureNativeRequestAllowed(request);
            WmiFanExperimentBoundary.MarkNativeStart(request);
            using var methodOutput = target.InvokeMethod(methodName, methodInput, invokeOptions)
                ?? throw new HpBiosCallException(
                    $"HP WMI method {methodName} returned no output.");

            WmiFanExperimentBoundary.MarkNativeReturned();
            EcWmiInvestigationTrace.Record(trace, "wmi.invoke.end", methodName);
            var resultData = methodOutput["OutData"] as ManagementBaseObject
                ?? throw new HpBiosCallException(
                    $"HP WMI method {methodName} returned no OutData object.");

            using (resultData)
            {
                var rawCode = resultData[ReturnCodeFieldName];
                if (rawCode is null)
                {
                    throw new HpBiosCallException(
                        "HP WMI response did not contain rwReturnCode.");
                }

                var responseData =
                    request.OutputSize == 0
                        ? Array.Empty<byte>()
                        : (resultData["Data"] as byte[] ?? Array.Empty<byte>()).ToArray();

                var response = new HpBiosResponse(Convert.ToInt32(rawCode), responseData);
                if (EcWmiInvestigationTrace.Enabled)
                {
                    EcWmiInvestigationTrace.Record(trace, "wmi.response", $"rc={response.ReturnCode};bytes={response.Data.Length}");
                    if (request.Command == 0x20008 && request.CommandType == 0x2D && response.Data.Length >= 2)
                        EcWmiInvestigationTrace.Record(trace, "wmi.fan-levels", $"raw={response.Data[0]}/{response.Data[1]}");
                }
                return response;
            }
        }
        catch (Exception ex)
        {
            if (EcWmiInvestigationTrace.Enabled)
                EcWmiInvestigationTrace.Record(trace, "wmi.failure", $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally { EcWmiInvestigationTrace.Record(trace, "wmi.send.end", ""); }
    }

    private static ManagementObject FindMethodInstance(ManagementScope scope)
    {
        var enumerationOptions = new System.Management.EnumerationOptions
        {
            Timeout = InvokeTimeout
        };

        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery($"SELECT * FROM {MethodClassName}"),
            enumerationOptions);

        using var results = searcher.Get();
        foreach (ManagementObject candidate in results)
        {
            var instanceName = candidate["InstanceName"]?.ToString();
            if (string.Equals(
                    instanceName,
                    MethodInstanceName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            candidate.Dispose();
        }

        throw new HpBiosCallException(
            $"HP BIOS WMI instance '{MethodInstanceName}' was not found.");
    }
}
