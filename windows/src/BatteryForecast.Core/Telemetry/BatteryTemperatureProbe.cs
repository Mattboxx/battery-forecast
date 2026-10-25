using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BatteryForecast.Core.Telemetry;

/// <summary>Read-only battery-class IOCTL. Unsupported/denied sensors remain unavailable.</summary>
static class BatteryTemperatureProbe
{
    public static double? ReadMaximum()
    {
        var batteryClass=new Guid("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
        var devices=SetupDiGetClassDevs(ref batteryClass,null,IntPtr.Zero,0x12);
        if(devices==new IntPtr(-1))return null;
        var values=new List<double>();
        try
        {
            for(uint index=0;index<16;index++)
            {
                var data=new InterfaceData { Size=(uint)Marshal.SizeOf<InterfaceData>() };
                if(!SetupDiEnumDeviceInterfaces(devices,IntPtr.Zero,ref batteryClass,index,ref data))break;
                SetupDiGetDeviceInterfaceDetail(devices,ref data,IntPtr.Zero,0,out var size,IntPtr.Zero);
                if(size<8 || size>65536)continue;
                var buffer=Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(buffer,IntPtr.Size==8?8:6);
                    if(!SetupDiGetDeviceInterfaceDetail(devices,ref data,buffer,size,out _,IntPtr.Zero))continue;
                    var path=Marshal.PtrToStringUni(buffer+4);
                    if(path is null)continue;
                    using var handle=CreateFile(path,0x80000000,3,IntPtr.Zero,3,0,IntPtr.Zero);
                    if(handle.IsInvalid)continue;
                    var tag=new byte[4];
                    if(!DeviceIoControl(handle,0x294040,new byte[4],4,tag,4,out var returned,IntPtr.Zero) || returned!=4 || BitConverter.ToUInt32(tag)==0)continue;
                    var request=new byte[12];
                    tag.CopyTo(request,0); BitConverter.GetBytes(2u).CopyTo(request,4); // BatteryTemperature
                    var temperature=new byte[4];
                    if(DeviceIoControl(handle,0x294044,request,12,temperature,4,out returned,IntPtr.Zero) && returned==4 &&
                        SensorMath.KelvinTenthsToCelsius(BitConverter.ToUInt32(temperature)) is double c) values.Add(c);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
        return values.Count>0?values.Max():null;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct InterfaceData { public uint Size; public Guid ClassGuid; public uint Flags; public IntPtr Reserved; }
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid guid,string? enumerator,IntPtr parent,uint flags);
    [DllImport("setupapi.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set,IntPtr device,ref Guid guid,uint index,ref InterfaceData data);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set,ref InterfaceData data,IntPtr detail,uint size,out uint required,IntPtr device);
    [DllImport("setupapi.dll")][return:MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern SafeFileHandle CreateFile(string path,uint access,uint sharing,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle file,uint code,byte[] input,uint inputSize,[Out] byte[] output,uint outputSize,out uint returned,IntPtr overlapped);
}
