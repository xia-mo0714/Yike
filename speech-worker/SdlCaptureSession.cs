using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
namespace WindowsTranslator {internal sealed class SdlCaptureSession:IDisposable {
 [StructLayout(LayoutKind.Sequential,Pack=8)]internal struct AudioSpec {internal int freq;internal ushort format;internal byte channels,silence;internal ushort samples,padding;internal uint size;internal IntPtr callback,userdata;}
 internal interface ICaptureDriver {string[] Enumerate();string GetDefaultName();AudioSpec Open(string name,Action<byte[]> callback);void Resume();bool IsConnected{get;}void Close();}
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate IntPtr NewStream(ushort source,byte channels,int rate,ushort destination,byte targetChannels,int targetRate);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int PutStream(IntPtr stream,IntPtr bytes,int count);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int StreamCount(IntPtr stream);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int GetStream(IntPtr stream,[Out]float[] output,int bytes);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void FreeStream(IntPtr stream);
 internal sealed class PcmConverter:IDisposable {
  private readonly PutStream put;private readonly StreamCount available,flush;private readonly GetStream get;private readonly FreeStream free;private IntPtr stream;private readonly int frameSize,rate,gridFrames;private readonly byte[] staging;private int staged;private long inputFrames,outputFrames;private bool completed;
  internal PcmConverter(NativeLibraryLoader loader,int rate,byte channels,ushort format){
   if(rate<8000||rate>192000||channels<1||channels>8||(format!=0x8120&&format!=0x8010))throw new InvalidDataException("capture_format_unsupported");
   loader.Load(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory).RuntimeRoot,"SDL2.dll");this.rate=rate;frameSize=channels*(format==0x8120?4:2);
   int a=rate,b=16000;while(b!=0){int remainder=a%b;a=b;b=remainder;}gridFrames=rate/a;
   // Whole rational resampling periods prevent SDL's per-Put truncation from
   // dropping fractional frames at every microphone callback (e.g. 44.1 kHz).
   int blockFrames=((rate/10+gridFrames-1)/gridFrames)*gridFrames;staging=new byte[blockFrames*frameSize];
   put=loader.Export<PutStream>("SDL2.dll","SDL_AudioStreamPut");available=loader.Export<StreamCount>("SDL2.dll","SDL_AudioStreamAvailable");flush=loader.Export<StreamCount>("SDL2.dll","SDL_AudioStreamFlush");get=loader.Export<GetStream>("SDL2.dll","SDL_AudioStreamGet");free=loader.Export<FreeStream>("SDL2.dll","SDL_FreeAudioStream");
   stream=loader.Export<NewStream>("SDL2.dll","SDL_NewAudioStream")(format,channels,rate,0x8120,1,16000);if(stream==IntPtr.Zero)throw new InvalidDataException("capture_converter_failed");
  }
  private void Put(byte[] bytes){if(bytes.Length==0)return;var pinned=GCHandle.Alloc(bytes,GCHandleType.Pinned);try{if(put(stream,pinned.AddrOfPinnedObject(),bytes.Length)!=0)throw new InvalidDataException("capture_converter_failed");}finally{pinned.Free();}}
  internal float[] Convert(byte[] bytes,bool finished){if(stream==IntPtr.Zero)throw new ObjectDisposedException("PcmConverter");if(completed)throw new InvalidOperationException("capture_converter_completed");if(bytes==null||bytes.Length%frameSize!=0||bytes.Length>1048576)throw new InvalidDataException("capture_bytes_invalid");
   inputFrames+=bytes.Length/frameSize;int offset=0;while(offset<bytes.Length){int copied=Math.Min(staging.Length-staged,bytes.Length-offset);Buffer.BlockCopy(bytes,offset,staging,staged,copied);staged+=copied;offset+=copied;if(staged==staging.Length){Put(staging);staged=0;}}
   // SDL 2.28.5 flushes the resampler's right padding only with a nonempty
   // staging buffer. A silent rational period triggers that path; trim it from
   // the output, never pad missing speech with invented samples.
   if(finished){if(staged>0){var tail=new byte[staged];Buffer.BlockCopy(staging,0,tail,0,staged);Put(tail);staged=0;}if(rate!=16000&&inputFrames>0)Put(new byte[frameSize*gridFrames]);if(flush(stream)!=0)throw new InvalidDataException("capture_converter_failed");completed=true;}
   int count=available(stream);if(count<0||count%4!=0||count>1048576)throw new InvalidDataException("capture_converter_limit");var result=new float[count/4];
   if(count>0&&get(stream,result,count)!=count)throw new InvalidDataException("capture_converter_failed");
   if(finished){long expected=(inputFrames*16000+rate-1)/rate,remaining=expected-outputFrames;if(remaining<0||remaining>result.Length)throw new InvalidDataException("capture_converter_frame_count");if(remaining<result.Length)Array.Resize(ref result,(int)remaining);}
   outputFrames+=result.Length;foreach(float value in result)if(float.IsNaN(value)||float.IsInfinity(value)||value<-1||value>1)throw new InvalidDataException("capture_sample_invalid");return result;
  }
  public void Dispose(){if(stream!=IntPtr.Zero){free(stream);stream=IntPtr.Zero;}}
 }
 private readonly object sync=new object(),lifecycle=new object();private readonly NativeLibraryLoader loader;private readonly ICaptureDriver driver;private BlockingCollection<byte[]> packets;private PcmConverter converter;private Thread processor;private bool running,disposed;private int overflow,generation;
 internal event Action<float[]> SamplesReceived;internal event Action<string,int> Failed;
 internal int Generation {get{lock(sync)return generation;}}
 internal SdlCaptureSession(NativeLibraryLoader loader):this(loader,new NativeDriver(loader)){}
 internal SdlCaptureSession(NativeLibraryLoader loader,ICaptureDriver driver){this.loader=loader;this.driver=driver;}
 private static string Normalized(string name){return System.Text.RegularExpressions.Regex.Replace((name??"").Normalize(NormalizationForm.FormKC).Trim(),@"\s+"," ").ToLowerInvariant();}
 internal SpeechCaptureEndpoint Start(string name){lock(lifecycle){lock(sync){if(disposed)throw new ObjectDisposedException("SdlCaptureSession");if(running)throw new InvalidOperationException("capture_already_started");}
  try{string[] names=driver.Enumerate();string selected=string.IsNullOrWhiteSpace(name)?driver.GetDefaultName():name;int index=-1,matches=0;
   for(int i=0;i<names.Length;i++)if(Normalized(names[i])==Normalized(selected)){index=i;matches++;}if(matches==0||string.IsNullOrWhiteSpace(selected))throw new InvalidDataException("capture_device_unavailable");if(matches!=1)index=-1;
   packets=new BlockingCollection<byte[]>(16);overflow=0;var spec=driver.Open(selected,CopyPacket);converter=new PcmConverter(loader,spec.freq,spec.channels,spec.format);
   int current;lock(sync){running=true;current=++generation;}processor=new Thread(()=>ProcessPackets(current)){IsBackground=true,Name="Yike capture conversion"};processor.Start();driver.Resume();return new SpeechCaptureEndpoint(index<0?selected:names[index],index);
  }catch{Stop();driver.Close();throw;}
 }}
 private void CopyPacket(byte[] bytes){var target=packets;if(target==null)return;try{if(bytes==null||bytes.Length>65536||!target.TryAdd(bytes))Interlocked.Exchange(ref overflow,1);}catch(InvalidOperationException){}}
 private void Publish(float[] samples){if(samples.Length==0)return;var handler=SamplesReceived;if(handler!=null)handler(samples);}
 private void ProcessPackets(int current){string failure=null;try{
  while(!packets.IsCompleted){byte[] bytes;if(packets.TryTake(out bytes,100)){Publish(converter.Convert(bytes,false));}
   if(Volatile.Read(ref overflow)!=0){failure="capture_queue_overflow";break;}
   bool active;lock(sync)active=running;if(active&&!driver.IsConnected){failure="capture_device_disconnected";break;}
  }
  if(failure!=null){byte[] remaining;for(int i=0;i<16&&packets.TryTake(out remaining);i++)Publish(converter.Convert(remaining,false));}
  Publish(converter.Convert(new byte[0],true));
 }catch(Exception){failure="capture_conversion_failed";}
 if(failure!=null){var handler=Failed;if(handler!=null)ThreadPool.QueueUserWorkItem(_=>{lock(sync){if(!running||current!=generation)return;}handler(failure,current);});}
 }
 internal void Stop(){lock(lifecycle){Thread thread;lock(sync){thread=processor;if(thread==Thread.CurrentThread)throw new InvalidOperationException("capture_stop_on_callback_thread");running=false;}driver.Close();if(packets!=null)packets.CompleteAdding();if(thread!=null)thread.Join();lock(sync){processor=null;}if(converter!=null){converter.Dispose();converter=null;}if(packets!=null){packets.Dispose();packets=null;}}}
 public void Dispose(){lock(lifecycle){lock(sync){if(disposed)return;disposed=true;}Stop();}}
 private sealed class NativeDriver:ICaptureDriver {
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int InitAudio(uint flags);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void QuitAudio(uint flags);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int CountAudio(int capture);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate IntPtr NameAudio(int index,int capture);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int DefaultAudio(out IntPtr name,out AudioSpec spec,int capture);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate uint OpenAudio(IntPtr name,int capture,ref AudioSpec desired,out AudioSpec obtained,int changes);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void AudioCallback(IntPtr user,IntPtr bytes,int count);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void CloseAudio(uint device);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void PauseAudio(uint device,int paused);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int StatusAudio(uint device);
  private readonly NativeLibraryLoader loader;private readonly InitAudio init;private readonly QuitAudio quit;private readonly CountAudio count;private readonly NameAudio name;private readonly DefaultAudio defaultAudio;private readonly OpenAudio open;private readonly CloseAudio close;private readonly PauseAudio pause;private readonly StatusAudio status;private readonly FreeStream free;
  private uint device;private bool initialized;private WhisperNative.CallbackLease callbackRoot;
  internal NativeDriver(NativeLibraryLoader loader){this.loader=loader;loader.Load(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory).RuntimeRoot,"SDL2.dll");
   init=loader.Export<InitAudio>("SDL2.dll","SDL_InitSubSystem");quit=loader.Export<QuitAudio>("SDL2.dll","SDL_QuitSubSystem");count=loader.Export<CountAudio>("SDL2.dll","SDL_GetNumAudioDevices");name=loader.Export<NameAudio>("SDL2.dll","SDL_GetAudioDeviceName");defaultAudio=loader.Export<DefaultAudio>("SDL2.dll","SDL_GetDefaultAudioInfo");open=loader.Export<OpenAudio>("SDL2.dll","SDL_OpenAudioDevice");close=loader.Export<CloseAudio>("SDL2.dll","SDL_CloseAudioDevice");pause=loader.Export<PauseAudio>("SDL2.dll","SDL_PauseAudioDevice");status=loader.Export<StatusAudio>("SDL2.dll","SDL_GetAudioDeviceStatus");free=loader.Export<FreeStream>("SDL2.dll","SDL_free");
  }
  public string[] Enumerate(){if(!initialized){if(init(0x10)!=0)throw new InvalidDataException("capture_initialization_failed");initialized=true;}int n=count(1);if(n<0||n>256)throw new InvalidDataException("capture_enumeration_failed");var names=new string[n];for(int i=0;i<n;i++)names[i]=WhisperNative.Utf8String(name(i,1));return names;}
  public string GetDefaultName(){IntPtr pointer;AudioSpec spec;if(defaultAudio(out pointer,out spec,1)!=0)throw new InvalidDataException("capture_default_unavailable");try{return WhisperNative.Utf8String(pointer);}finally{if(pointer!=IntPtr.Zero)free(pointer);}}
  public AudioSpec Open(string selected,Action<byte[]> copy){AudioCallback callback=(user,bytes,length)=>{try{if(length<=0)return;if(length>65536){copy(null);return;}var packet=new byte[length];Marshal.Copy(bytes,packet,0,length);copy(packet);}catch(Exception){try{copy(null);}catch(Exception){}}};
   callbackRoot=new WhisperNative.CallbackLease(callback);var desired=new AudioSpec{freq=16000,format=0x8120,channels=1,samples=1024,callback=callbackRoot.Pointer};AudioSpec actual;
   using(var label=new WhisperNative.Utf8(selected))device=open(label.Pointer,1,ref desired,out actual,15);if(device==0)throw new InvalidDataException("capture_open_failed");return actual;
  }
  public bool IsConnected{get{return device!=0&&status(device)==1;}}public void Resume(){pause(device,0);}
  public void Close(){uint owned=device;device=0;if(owned!=0)close(owned);if(callbackRoot!=null){callbackRoot.Dispose();callbackRoot=null;}if(initialized){quit(0x10);initialized=false;}GC.KeepAlive(loader);}
 }
}}
