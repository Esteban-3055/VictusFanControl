using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VictusFanControl.App;

internal sealed record HyperXInterface(string Path,ushort Page,ushort Usage,int InputBytes,int OutputBytes);
internal static class HyperXHid
{
    [StructLayout(LayoutKind.Sequential)] private struct InterfaceData {public int Size;public Guid Guid;public int Flags;public UIntPtr Reserved;}
    [StructLayout(LayoutKind.Sequential)] private struct Attributes {public int Size;public ushort Vendor,Product,Version;}
    [StructLayout(LayoutKind.Sequential)] private struct Caps
    {
        public ushort Usage,Page,Input,Output,Feature;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=17)] public ushort[] Reserved;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=10)] public ushort[] Counts;
    }
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr SetupDiGetClassDevsW(ref Guid guid,IntPtr enumerator,IntPtr parent,uint flags);
    [DllImport("setupapi.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set,IntPtr dev,ref Guid guid,uint index,ref InterfaceData data);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set,ref InterfaceData data,IntPtr detail,uint size,out uint required,IntPtr dev);
    [DllImport("setupapi.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("hid.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool HidD_GetAttributes(SafeFileHandle file,ref Attributes attributes);
    [DllImport("hid.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool HidD_GetPreparsedData(SafeFileHandle file,out IntPtr data);
    [DllImport("hid.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data,out Caps caps);
    internal static IReadOnlyList<HyperXInterface> Enumerate()
    {
        HidD_GetHidGuid(out var guid);var set=SetupDiGetClassDevsW(ref guid,IntPtr.Zero,IntPtr.Zero,0x12);
        if(set==new IntPtr(-1))throw new Win32Exception();
        var result=new List<HyperXInterface>();
        try
        {
            for(uint i=0;;i++)
            {
                var data=new InterfaceData{Size=Marshal.SizeOf<InterfaceData>()};
                if(!SetupDiEnumDeviceInterfaces(set,IntPtr.Zero,ref guid,i,ref data))
                {if(Marshal.GetLastWin32Error()==259)break;throw new Win32Exception();}
                SetupDiGetDeviceInterfaceDetailW(set,ref data,IntPtr.Zero,0,out uint needed,IntPtr.Zero);
                if(needed is <8 or >65536)continue;
                var detail=Marshal.AllocHGlobal((int)needed);
                try
                {
                    Marshal.WriteInt32(detail,IntPtr.Size==8?8:6);
                    if(!SetupDiGetDeviceInterfaceDetailW(set,ref data,detail,needed,out _,IntPtr.Zero))continue;
                    var path=Marshal.PtrToStringUni(IntPtr.Add(detail,4));
                    if(path is null||!path.Contains("vid_0951&pid_",StringComparison.OrdinalIgnoreCase))continue;
                    using var handle=CreateFileW(path,0,3,IntPtr.Zero,3,0,IntPtr.Zero);
                    var attrs=new Attributes{Size=Marshal.SizeOf<Attributes>()};
                    if(handle.IsInvalid||!HidD_GetAttributes(handle,ref attrs)||attrs.Vendor!=0x0951||attrs.Product is not(0x1723 or 0x16c4))continue;
                    if(!HidD_GetPreparsedData(handle,out var prep))continue;
                    try
                    {
                        if(HidP_GetCaps(prep,out var c)!=0x110000)continue;
                        if((c.Page is 0xff90 or 0xff53&&c.Usage==0x303&&c.Input is 15 or 20&&c.Output==20)||
                            (c.Page==0xffc0&&c.Usage==1&&c.Input==2&&c.Output==0))
                            result.Add(new(path,c.Page,c.Usage,c.Input,c.Output));
                    }
                    finally{HidD_FreePreparsedData(prep);}
                }
                finally{Marshal.FreeHGlobal(detail);}
            }
            return result;
        }
        finally{SetupDiDestroyDeviceInfoList(set);}
    }
    internal static FileStream Open(HyperXInterface device,bool write)
    {
        var h=CreateFileW(device.Path,write?0xc0000000u:0x80000000u,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
        if(h.IsInvalid){h.Dispose();throw new Win32Exception();}
        try{return new FileStream(h,write?FileAccess.ReadWrite:FileAccess.Read,Math.Max(20,device.InputBytes),true);}
        catch{h.Dispose();throw;}
    }
}
