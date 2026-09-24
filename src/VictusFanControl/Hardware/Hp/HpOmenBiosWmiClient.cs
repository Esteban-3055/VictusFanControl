using System.Management;

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

    public int Send(HpBiosRequest request) =>
        SendWithResponse(request).ReturnCode;

    public HpBiosResponse SendWithResponse(HpBiosRequest request)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("HP BIOS/WMI access requires Windows.");
        }

        if (request.OutputSize is not (0 or 4 or 128 or 1024 or 4096))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Unsupported HP BIOS output size.");
        }

        var scope = new ManagementScope(NamespacePath);
        scope.Connect();

        using var dataClass = new ManagementClass(
            scope,
            new ManagementPath(DataClassName),
            options: null);

        using var data = dataClass.CreateInstance()
            ?? throw new HpBiosCallException("Could not create hpqBDataIn.");

        data["Sign"] = Signature.ToArray();
        data["Command"] = request.Command;
        data["CommandType"] = request.CommandType;
        data["Size"] = (uint)request.Payload.Length;
        data[DataFieldName] = request.Payload.ToArray();

        using var target = FindMethodInstance(scope);
        var methodName = $"hpqBIOSInt{request.OutputSize}";

        using var methodInput = target.GetMethodParameters(methodName)
            ?? throw new HpBiosCallException(
                $"HP WMI method {methodName} does not expose input parameters.");

        methodInput["InData"] = data;

        var invokeOptions = new InvokeMethodOptions
        {
            Timeout = InvokeTimeout
        };

        using var methodOutput = target.InvokeMethod(methodName, methodInput, invokeOptions)
            ?? throw new HpBiosCallException(
                $"HP WMI method {methodName} returned no output.");

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

            return new HpBiosResponse(
                Convert.ToInt32(rawCode),
                responseData);
        }
    }

    private static ManagementObject FindMethodInstance(ManagementScope scope)
    {
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery($"SELECT * FROM {MethodClassName}"));

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
