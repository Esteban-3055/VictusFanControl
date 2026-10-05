namespace VictusFanControl.Hardware.Nvidia;

internal readonly record struct NvmlFieldUnsignedCallResult(
    bool ExportAvailable,
    int? QueryResult,
    int FieldResult,
    uint FieldId,
    int ValueType,
    bool UnsignedValueDecoded,
    ulong Value)
{
    internal bool IsSuccess =>
        ExportAvailable &&
        QueryResult == 0 &&
        FieldResult == 0 &&
        UnsignedValueDecoded;
}

internal readonly record struct NvmlGpuPowerFieldSnapshot(
    bool ExportAvailable,
    int? QueryResult,
    NvmlFieldUnsignedCallResult MinLimit,
    NvmlFieldUnsignedCallResult MaxLimit,
    NvmlFieldUnsignedCallResult DefaultLimit,
    NvmlFieldUnsignedCallResult CurrentLimit,
    NvmlFieldUnsignedCallResult RequestedLimit);

/// <summary>
/// Read-only NVML field-value transport for the power-limit fields.
///
/// NVML_FI_DEV_POWER_REQUESTED_LIMIT is documented by NVIDIA as the power
/// limit requested by NVML or another userspace client. Unlike the legacy
/// GetPowerManagementLimit call, this field may therefore provide the exact
/// userspace-requested value needed for ownership qualification.
/// </summary>
internal interface INvmlGpuPowerFieldReadTransport
{
    bool PowerFieldValuesExportAvailable { get; }

    NvmlGpuPowerFieldSnapshot ReadPowerFieldSnapshotOnce();
}
