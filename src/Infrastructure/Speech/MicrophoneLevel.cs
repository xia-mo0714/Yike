using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Speech.Recognition;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
namespace WindowsTranslator {
internal sealed class MicrophoneLevel : ISpeechCaptureMeter
{
	[ComImport]
	[Guid ("BCDE0395-E52F-467C-8E3D-C4579291692E")]
	private class Enumerator
	{
	}

	[ComImport]
	[Guid ("A95664D2-9614-4F35-A746-DE8DB63617E6")]
	[InterfaceType (ComInterfaceType.InterfaceIsIUnknown)]
	private interface IDevices
	{
		[PreserveSig]
		int EnumEndpoints (int flow, uint mask, out IntPtr devices);

		[PreserveSig]
		int GetDefaultEndpoint (int flow, int role, out IDevice device);
	}

	[ComImport]
	[InterfaceType (ComInterfaceType.InterfaceIsIUnknown)]
	[Guid ("D666063F-1587-4E43-81F1-B948E807363F")]
	private interface IDevice
	{
		[PreserveSig]
		int Activate (ref Guid id, uint context, IntPtr parameters, [MarshalAs (UnmanagedType.IUnknown)] out object instance);
		[PreserveSig] int OpenPropertyStore(uint access,out IPropertyStore store);
		[PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
		[PreserveSig] int GetState(out uint state);
	}

	[ComImport]
	[InterfaceType (ComInterfaceType.InterfaceIsIUnknown)]
	[Guid ("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
	private interface IMeter
	{
		[PreserveSig]
		int GetPeakValue (out float peak);
	}

	[StructLayout(LayoutKind.Sequential)] private struct PropertyKey {public Guid Format;public uint Id;}
	[StructLayout(LayoutKind.Explicit,Size=24)] private struct PropVariant {[FieldOffset(0)] public ushort Type;[FieldOffset(8)] public IntPtr Pointer;}
	[ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPropertyStore {
		[PreserveSig] int GetCount(out uint count);
		[PreserveSig] int GetAt(uint index,out PropertyKey key);
		[PreserveSig] int GetValue(ref PropertyKey key,out PropVariant value);
	}
	[ComImport,Guid("0BD7A1BE-7A1A-44DB-8397-C0A8DDF70CC6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IDeviceCollection {
		[PreserveSig] int GetCount(out uint count);
		[PreserveSig] int Item(uint index,out IDevice device);
	}
	[DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
	private readonly object sync=new object();
	private IMeter meter;
	public string DeviceId {get;private set;}
	public string DeviceName {get;private set;}
	public string[] CaptureNames {get {
		IDevices devices=(IDevices)new Enumerator();IntPtr pointer=IntPtr.Zero;IDeviceCollection collection=null;var names=new List<string>();
		try {Marshal.ThrowExceptionForHR(devices.EnumEndpoints(1,1,out pointer));collection=(IDeviceCollection)Marshal.GetObjectForIUnknown(pointer);uint count;Marshal.ThrowExceptionForHR(collection.GetCount(out count));if(count>256)throw new InvalidOperationException("capture_endpoint_limit");
			for(uint i=0;i<count;i++){IDevice device=null;IPropertyStore store=null;try{Marshal.ThrowExceptionForHR(collection.Item(i,out device));Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0,out store));PropertyKey key=new PropertyKey{Format=new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),Id=14};PropVariant value;Marshal.ThrowExceptionForHR(store.GetValue(ref key,out value));try{names.Add(value.Type==31&&value.Pointer!=IntPtr.Zero?Marshal.PtrToStringUni(value.Pointer):"");}finally{PropVariantClear(ref value);}}finally{if(store!=null)Marshal.ReleaseComObject(store);if(device!=null)Marshal.ReleaseComObject(device);}}
			return names.ToArray();
		}finally{if(collection!=null)Marshal.ReleaseComObject(collection);if(pointer!=IntPtr.Zero)Marshal.Release(pointer);Marshal.ReleaseComObject(devices);}
	}}

	public MicrophoneLevel ()
	{
		IDevices devices = (IDevices)new Enumerator ();
		IDevice device = null;
		IPropertyStore store=null;
		try {
			Marshal.ThrowExceptionForHR (devices.GetDefaultEndpoint (1, 0, out device));
			string deviceId;Marshal.ThrowExceptionForHR(device.GetId(out deviceId));DeviceId=deviceId;
			Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0,out store));
			PropertyKey key=new PropertyKey {Format=new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),Id=14};PropVariant value;
			Marshal.ThrowExceptionForHR(store.GetValue(ref key,out value));
			try {DeviceName=value.Type==31 && value.Pointer!=IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : "";} finally {PropVariantClear(ref value);}
			Guid id = typeof(IMeter).GUID;
			object instance;
			Marshal.ThrowExceptionForHR (device.Activate (ref id, 23u, IntPtr.Zero, out instance));
			meter = (IMeter)instance;
		} finally {
			if(store!=null) Marshal.ReleaseComObject(store);
			if (device != null) {
				Marshal.ReleaseComObject (device);
			}
			Marshal.ReleaseComObject (devices);
		}
	}

	public int Read ()
	{
		return Math.Max (0, Math.Min (100, (int)Math.Round (ReadPeak() * 100)));
	}
	public double ReadPeak() {lock(sync) {if(meter==null) throw new ObjectDisposedException("MicrophoneLevel");float peak;Marshal.ThrowExceptionForHR(meter.GetPeakValue(out peak));return peak;}}
	public bool DefaultDeviceChanged() {
		IDevices devices=(IDevices)new Enumerator();IDevice device=null;
		try {Marshal.ThrowExceptionForHR(devices.GetDefaultEndpoint(1,0,out device));string id;Marshal.ThrowExceptionForHR(device.GetId(out id));return !string.Equals(id,DeviceId,StringComparison.Ordinal);}
		finally {if(device!=null) Marshal.ReleaseComObject(device);Marshal.ReleaseComObject(devices);}
	}

	public void Dispose ()
	{
		lock(sync) {
		if (meter != null) {
			Marshal.ReleaseComObject (meter);
			meter = null;
		}
		}
	}
}

}
