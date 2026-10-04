#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <setupapi.h>
#include <initguid.h>
#include <stdio.h>
#include "contract.h"
#include "identity.h"
DEFINE_GUID(GUID_DEVINTERFACE_VFC_ACPI_PROBE,
    0x7398c2f1,0x15ca,0x4b5b,0x89,0xe8,0x16,0xb6,0x14,0x28,0x0a,0x5d);
/* Same bounded parser as the kernel gate, without loading any driver. */
static int IdentityOnly(void){
    UINT required=GetSystemFirmwareTable('RSMB',0,NULL,0),actual;
    unsigned char *raw;int match;
    if(required<8 || required>1024*1024){puts("{\"identity_match\":false,\"reason\":\"SMBIOS unavailable or size outside limit\",\"driver_loaded\":false}");return 12;}
    raw=HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,required);
    if(!raw)return 12;
    actual=GetSystemFirmwareTable('RSMB',0,raw,required);
    match=actual==required && VfcMatchRawSmbios(raw,actual);
    SecureZeroMemory(raw,required);HeapFree(GetProcessHeap(),0,raw);
    printf("{\"identity_match\":%s,\"table_bytes\":%u,\"driver_loaded\":false,\"acpi_evaluated\":false,\"production_ready\":false}\n",match?"true":"false",actual);
    return match?0:12;
}
/* This build only offers the _STA control. No EC field command or installer. */
int main(int argc,char **argv){
    HDEVINFO set;SP_DEVICE_INTERFACE_DATA item,extra;
    PSP_DEVICE_INTERFACE_DETAIL_DATA_A detail=NULL;
    DWORD required=0,bytes=0,error;HANDLE handle;BOOL ok;
    VFC_READ_INPUT input={VFC_PROBE_VERSION,0};VFC_READ_OUTPUT output={0};
    if(argc==2 && !strcmp(argv[1],"--identity"))return IdentityOnly();
    if(argc!=2 || strcmp(argv[1],"--control")){
        puts("Usage: probe-client.exe --identity | --control (requires a separately reviewed PnP deployment). No driver installation is performed.");return 2;
    }
    set=SetupDiGetClassDevsA(&GUID_DEVINTERFACE_VFC_ACPI_PROBE,NULL,NULL,DIGCF_PRESENT|DIGCF_DEVICEINTERFACE);
    if(set==INVALID_HANDLE_VALUE)return 3;
    ZeroMemory(&item,sizeof(item));item.cbSize=sizeof(item);
    if(!SetupDiEnumDeviceInterfaces(set,NULL,&GUID_DEVINTERFACE_VFC_ACPI_PROBE,0,&item)){
        error=GetLastError();SetupDiDestroyDeviceInfoList(set);
        printf("No probe interface; Win32=%lu. This is not evidence of unsupported ACPI fields.\n",error);return 4;
    }
    ZeroMemory(&extra,sizeof(extra));extra.cbSize=sizeof(extra);
    if(SetupDiEnumDeviceInterfaces(set,NULL,&GUID_DEVINTERFACE_VFC_ACPI_PROBE,1,&extra) || GetLastError()!=ERROR_NO_MORE_ITEMS){
        SetupDiDestroyDeviceInfoList(set);puts("Ambiguous probe interfaces; stopped.");return 5;
    }
    SetupDiGetDeviceInterfaceDetailA(set,&item,NULL,0,&required,NULL);
    if(GetLastError()!=ERROR_INSUFFICIENT_BUFFER || required<sizeof(*detail)){
        SetupDiDestroyDeviceInfoList(set);return 6;
    }
    detail=HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,required);
    if(!detail){SetupDiDestroyDeviceInfoList(set);return 7;}
    detail->cbSize=sizeof(*detail);
    ok=SetupDiGetDeviceInterfaceDetailA(set,&item,detail,required,NULL,NULL);
    if(!ok){HeapFree(GetProcessHeap(),0,detail);SetupDiDestroyDeviceInfoList(set);return 8;}
    handle=CreateFileA(detail->DevicePath,GENERIC_READ,0,NULL,OPEN_EXISTING,0,NULL);
    HeapFree(GetProcessHeap(),0,detail);SetupDiDestroyDeviceInfoList(set);
    if(handle==INVALID_HANDLE_VALUE){printf("Open failed: %lu\n",GetLastError());return 9;}
    ok=DeviceIoControl(handle,VFC_IOCTL_READ,&input,sizeof(input),&output,sizeof(output),&bytes,NULL);
    error=ok?0:GetLastError();CloseHandle(handle);
    if(!ok || bytes!=sizeof(output) || output.Version!=1 || output.Selector!=0 || output.Valid>1 || output.CapturedBytes>20){
        printf("Invalid control response: Win32=%lu bytes=%lu\n",error,bytes);return 10;
    }
    printf("{\"object\":\"_STA\",\"native_status\":%u,\"valid\":%u,\"value\":%u,\"bytes\":%u,\"ticks\":%llu,\"frequency\":%llu,\"production_ready\":false}\n",
        output.NativeStatus,output.Valid,output.Value,output.NativeBytes,
        (unsigned long long)output.DurationTicks,(unsigned long long)output.Frequency);
    return output.Valid && output.NativeStatus==0 && output.Value==15?0:11;
}
