#include <ntddk.h>
#pragma warning(push)
#pragma warning(disable:4324) /* WDK headers intentionally pad aligned structures. */
#include <wdf.h>
#pragma warning(pop)
#include <acpiioct.h>
#include <aux_klib.h>
#include <initguid.h>
#include <devpkey.h>
#include "contract.h"
#include "identity.h"
#include "lifecycle.h"
DEFINE_GUID(GUID_DEVINTERFACE_VFC_ACPI_PROBE,
    0x7398c2f1,0x15ca,0x4b5b,0x89,0xe8,0x16,0xb6,0x14,0x28,0x0a,0x5d);
C_ASSERT(sizeof(VFC_READ_INPUT)==8);
C_ASSERT(sizeof(VFC_READ_OUTPUT)==64);
C_ASSERT(sizeof(ACPI_EVAL_OUTPUT_BUFFER)==20);
C_ASSERT(ACPI_METHOD_ARGUMENT_INTEGER==0);
C_ASSERT(VFC_IOCTL_READ==CTL_CODE(FILE_DEVICE_UNKNOWN,0x800,METHOD_BUFFERED,FILE_READ_DATA));
typedef struct { VFC_PROBE_STATE Probe; } DEVICE_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT,Context);
DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD DeviceAdd;
EVT_WDF_DEVICE_PREPARE_HARDWARE Prepare;
EVT_WDF_DEVICE_RELEASE_HARDWARE Release;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL ReadObject;
EVT_WDF_IO_IN_CALLER_CONTEXT Caller;

NTSTATUS DriverEntry(PDRIVER_OBJECT driver, PUNICODE_STRING path) {
    WDF_DRIVER_CONFIG config;
    NTSTATUS status=AuxKlibInitialize();
    if(!NT_SUCCESS(status))return status;
    WDF_DRIVER_CONFIG_INIT(&config,DeviceAdd);
    return WdfDriverCreate(driver,path,WDF_NO_OBJECT_ATTRIBUTES,&config,WDF_NO_HANDLE);
}
/* Metadata only; no ACPI method evaluation and no firmware identifiers logged. */
static BOOLEAN QualifiedIdentity(WDFDEVICE device) {
    WDF_DEVICE_PROPERTY_DATA property;
    DEVPROPTYPE type=0;
    WCHAR instance[64]={0};
    static const WCHAR expected[]=L"ACPI\\PNP0C09\\1";
    ULONG required=0,actual=0;
    NTSTATUS status;
    unsigned char *raw;
    BOOLEAN match;
    WDF_DEVICE_PROPERTY_DATA_INIT(&property,&DEVPKEY_Device_InstanceId);
    status=WdfDeviceQueryPropertyEx(device,&property,sizeof(instance),instance,&actual,&type);
    if(!NT_SUCCESS(status) || type!=DEVPROP_TYPE_STRING || actual!=sizeof(expected) ||
        RtlCompareMemory(instance,expected,sizeof(expected))!=sizeof(expected))return FALSE;
    status=AuxKlibGetSystemFirmwareTable('RSMB',0,NULL,0,&required);
    if((!NT_SUCCESS(status) && status!=STATUS_BUFFER_TOO_SMALL) || required<8 || required>1024*1024)return FALSE;
    raw=ExAllocatePool2(POOL_FLAG_PAGED,required,'IfCV');
    if(!raw)return FALSE;
    status=AuxKlibGetSystemFirmwareTable('RSMB',0,raw,required,&actual);
    match=NT_SUCCESS(status) && actual==required && VfcMatchRawSmbios(raw,actual);
    RtlSecureZeroMemory(raw,required);ExFreePoolWithTag(raw,'IfCV');
    return match;
}
/* Thin WDF adapters: the exact ownership sequence is host-testable. */
static int ProbeIdentity(void *device) { return QualifiedIdentity(device); }
static int ProbeCreate(void *device,void **result) {
    WDF_OBJECT_ATTRIBUTES attrs;
    WDFIOTARGET target=NULL;
    NTSTATUS status;
    WDF_OBJECT_ATTRIBUTES_INIT(&attrs);attrs.ParentObject=device;
    status=WdfIoTargetCreate(device,&attrs,&target);
    if(!NT_SUCCESS(status))return 0;
    *result=target;return 1;
}
static int ProbeOpen(void *device,void *target) {
    WDF_IO_TARGET_OPEN_PARAMS open;
    /* Use this stack's PDO, never an observed/guessed private name. */
    WDF_IO_TARGET_OPEN_PARAMS_INIT_EXISTING_DEVICE(&open,WdfDeviceWdmGetPhysicalDevice(device));
    return NT_SUCCESS(WdfIoTargetOpen(target,&open));
}
static void ProbeClose(void *target) { WdfIoTargetClose(target); }
static void ProbeDelete(void *target) { WdfObjectDelete(target); }
static const VFC_PROBE_OPS ProbeOps={ProbeIdentity,ProbeCreate,ProbeOpen,ProbeClose,ProbeDelete};
NTSTATUS Prepare(WDFDEVICE device,WDFCMRESLIST raw,WDFCMRESLIST translated) {
    UNREFERENCED_PARAMETER(raw);UNREFERENCED_PARAMETER(translated);
    VfcProbePrepare(&Context(device)->Probe,device,&ProbeOps);
    return STATUS_SUCCESS; /* Optional diagnostic setup must not fail EC start. */
}
NTSTATUS Release(WDFDEVICE device,WDFCMRESLIST translated) {
    UNREFERENCED_PARAMETER(translated);
    VfcProbeRelease(&Context(device)->Probe,&ProbeOps);
    return STATUS_SUCCESS;
}
/* Block arbitrary user IOCTLs before they can pass through the filter. */
VOID Caller(WDFDEVICE device,WDFREQUEST request) {
    WDF_REQUEST_PARAMETERS p;
    NTSTATUS status;
    WDF_REQUEST_PARAMETERS_INIT(&p); WdfRequestGetParameters(request,&p);
    if (WdfRequestGetRequestorMode(request)==UserMode &&
        ((p.Type==WdfRequestTypeDeviceControl && p.Parameters.DeviceIoControl.IoControlCode!=VFC_IOCTL_READ) ||
         p.Type==WdfRequestTypeRead || p.Type==WdfRequestTypeWrite || p.Type==WdfRequestTypeDeviceControlInternal)) {
        WdfRequestComplete(request,STATUS_INVALID_DEVICE_REQUEST); return;
    }
    if(p.Type==WdfRequestTypeDeviceControl && p.Parameters.DeviceIoControl.IoControlCode==VFC_IOCTL_READ &&
        (!Context(device)->Probe.Qualified || Context(device)->Probe.Faulted)) {
        WdfRequestComplete(request,STATUS_DEVICE_NOT_READY); return;
    }
    status=WdfDeviceEnqueueRequest(device,request);
    if (!NT_SUCCESS(status)) WdfRequestComplete(request,status);
}
NTSTATUS DeviceAdd(WDFDRIVER driver,PWDFDEVICE_INIT init) {
    WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES attrs;
    WDF_PNPPOWER_EVENT_CALLBACKS pnp;
    WDF_IO_QUEUE_CONFIG queue;
    WDFMEMORY memory;
    WCHAR *ids;
    size_t bytes,i;
    BOOLEAN match=FALSE;
    NTSTATUS status;
    DECLARE_CONST_UNICODE_STRING(sddl,L"D:P(A;;GA;;;SY)(A;;GA;;;BA)");
    UNREFERENCED_PARAMETER(driver);
    status=WdfFdoInitAllocAndQueryProperty(init,DevicePropertyHardwareID,NonPagedPoolNx,
        WDF_NO_OBJECT_ATTRIBUTES,&memory);
    if (!NT_SUCCESS(status)) return status;
    ids=WdfMemoryGetBuffer(memory,&bytes);
    for(i=0;i<bytes/sizeof(WCHAR);){
        size_t start=i;
        while(i<bytes/sizeof(WCHAR) && ids[i]) ++i;
        if(i==bytes/sizeof(WCHAR)) break;
        if (i-start==12 && RtlCompareMemory(ids+start,L"ACPI\\PNP0C09",24)==24) match=TRUE;
        ++i;
    }
    WdfObjectDelete(memory);
    if(!match) return STATUS_NOT_SUPPORTED;
    WdfFdoInitSetFilter(init);
    WdfDeviceInitSetCharacteristics(init,FILE_AUTOGENERATED_DEVICE_NAME|FILE_DEVICE_SECURE_OPEN,TRUE);
    status=WdfDeviceInitAssignSDDLString(init,&sddl);
    if(!NT_SUCCESS(status)) return status;
    WdfDeviceInitSetIoInCallerContextCallback(init,Caller);
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnp);
    pnp.EvtDevicePrepareHardware=Prepare; pnp.EvtDeviceReleaseHardware=Release;
    WdfDeviceInitSetPnpPowerEventCallbacks(init,&pnp);
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attrs,DEVICE_CONTEXT);
    attrs.ExecutionLevel=WdfExecutionLevelPassive;
    status=WdfDeviceCreate(&init,&attrs,&device);
    if(!NT_SUCCESS(status)) return status;
    if(!QualifiedIdentity(device))return STATUS_SUCCESS; /* No interface or ACPI target on rejection. */
    Context(device)->Probe.Qualified=TRUE;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queue,WdfIoQueueDispatchSequential);
    queue.EvtIoDeviceControl=ReadObject;
    status=WdfIoQueueCreate(device,&queue,WDF_NO_OBJECT_ATTRIBUTES,WDF_NO_HANDLE);
    if(!NT_SUCCESS(status)) { Context(device)->Probe.Faulted=TRUE; return STATUS_SUCCESS; }
    status=WdfDeviceCreateDeviceInterface(device,&GUID_DEVINTERFACE_VFC_ACPI_PROBE,NULL);
    if(!NT_SUCCESS(status))Context(device)->Probe.Faulted=TRUE;
    return STATUS_SUCCESS; /* Optional queue/interface failure leaves the probe inactive. */
}
VOID ReadObject(WDFQUEUE queue,WDFREQUEST request,size_t outLength,size_t inLength,ULONG code) {
    WDFDEVICE device=WdfIoQueueGetDevice(queue);
    VFC_PROBE_STATE *ctx=&Context(device)->Probe;
    VFC_READ_INPUT *in;
    VFC_READ_OUTPUT *out;
    ACPI_EVAL_INPUT_BUFFER nativeIn;
    ACPI_EVAL_OUTPUT_BUFFER nativeOut;
    WDF_MEMORY_DESCRIPTOR input,output;
    WDF_REQUEST_SEND_OPTIONS options;
    LARGE_INTEGER begin,end,frequency;
    ULONG_PTR returned=0;
    ULONG selector,maximum;
    uint32_t value=0;
    NTSTATUS status;
    if(code!=VFC_IOCTL_READ){
        if(WdfRequestGetRequestorMode(request)==KernelMode){
            WdfRequestFormatRequestUsingCurrentType(request);
            WDF_REQUEST_SEND_OPTIONS_INIT(&options,WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
            if(WdfRequestSend(request,WdfDeviceGetIoTarget(device),&options)) return;
            status=WdfRequestGetStatus(request);
        } else status=STATUS_INVALID_DEVICE_REQUEST;
        WdfRequestComplete(request,status); return;
    }
    if(inLength!=sizeof(*in) || outLength!=sizeof(*out)) { WdfRequestComplete(request,STATUS_INFO_LENGTH_MISMATCH); return; }
    status=WdfRequestRetrieveInputBuffer(request,sizeof(*in),(PVOID*)&in,NULL);
    if(!NT_SUCCESS(status)){WdfRequestComplete(request,status);return;}
    selector=in->Selector;
    if(in->Version!=VFC_PROBE_VERSION || selector>=VFC_SELECTOR_COUNT){WdfRequestComplete(request,STATUS_INVALID_PARAMETER);return;}
    /* This source-only build exposes _STA control, with EC fields disabled. */
    if(selector && !VFC_FIELD_PROBES_ENABLED){WdfRequestComplete(request,STATUS_NOT_SUPPORTED);return;}
    if(!ctx->Qualified || !ctx->Target || ctx->Faulted || (ctx->Used&(1u<<selector)) || (selector && !ctx->ControlPassed)){
        WdfRequestComplete(request,STATUS_DEVICE_NOT_READY);return;
    }
    status=WdfRequestRetrieveOutputBuffer(request,sizeof(*out),(PVOID*)&out,NULL);
    if(!NT_SUCCESS(status)){WdfRequestComplete(request,status);return;}
    RtlZeroMemory(out,sizeof(*out));out->Version=VFC_PROBE_VERSION;out->Selector=selector;
    ctx->Used|=1u<<selector; /* Consume before sending: never auto-retry a call. */
    RtlZeroMemory(&nativeIn,sizeof(nativeIn));RtlZeroMemory(&nativeOut,sizeof(nativeOut));
    nativeIn.Signature=ACPI_EVAL_INPUT_BUFFER_SIGNATURE;
    RtlCopyMemory(nativeIn.MethodName,VfcNames[selector],4);
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&input,&nativeIn,sizeof(nativeIn));
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&output,&nativeOut,sizeof(nativeOut));
    WDF_REQUEST_SEND_OPTIONS_INIT(&options,WDF_REQUEST_SEND_OPTION_TIMEOUT);
    WDF_REQUEST_SEND_OPTIONS_SET_TIMEOUT(&options,WDF_REL_TIMEOUT_IN_SEC(5));
    begin=KeQueryPerformanceCounter(&frequency);
    status=WdfIoTargetSendIoctlSynchronously(ctx->Target,NULL,IOCTL_ACPI_EVAL_METHOD,&input,&output,&options,&returned);
    end=KeQueryPerformanceCounter(NULL);
    out->NativeStatus=(ULONG)status;out->NativeBytes=(ULONG)returned;
    out->CapturedBytes=(ULONG)(returned>sizeof(nativeOut)?sizeof(nativeOut):returned);
    RtlCopyMemory(out->NativeData,&nativeOut,out->CapturedBytes);
    out->DurationTicks=(uint64_t)(end.QuadPart-begin.QuadPart);out->Frequency=(uint64_t)frequency.QuadPart;
    maximum=selector>=4?1:255;
    if(NT_SUCCESS(status) && VfcParseInteger((const unsigned char*)&nativeOut,(size_t)returned,
        ACPI_EVAL_OUTPUT_BUFFER_SIGNATURE,maximum,&value) && (selector || value==15)){
        out->Valid=1;out->Value=value;if(!selector)ctx->ControlPassed=TRUE;
    }else ctx->Faulted=TRUE;
    /* Transport completion and data validity are deliberately separate. */
    WdfRequestCompleteWithInformation(request,STATUS_SUCCESS,sizeof(*out));
}
