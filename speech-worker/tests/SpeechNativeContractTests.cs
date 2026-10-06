using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
internal static class SpeechNativeContractTests {
 internal static void RunSpeechNativeContractTests(){
  var defaults=new WhisperNative.ContextParams{use_gpu=1,flash_attn=1,gpu_device=7,dtw_token_timestamps=1,dtw_n_top=-1,dtw_mem_size=new UIntPtr(134217728)};
  var cpu=WhisperContext.CpuParameters(defaults);
  SpeechWorkerTests.Check(cpu.use_gpu==0&&cpu.dtw_token_timestamps==0,"CpuContextDisablesGpuAndUnusedTokenTimestamps");
  SpeechWorkerTests.Check(cpu.flash_attn==1&&cpu.gpu_device==7&&cpu.dtw_n_top==-1&&cpu.dtw_mem_size.ToUInt64()==134217728,"CpuContextPreservesNonGpuRuntimeDefaults");
  defaults.flash_attn=0;
  SpeechWorkerTests.Check(WhisperContext.CpuParameters(defaults).flash_attn==0,"CpuContextDoesNotInventUnsupportedOptimization");
  SpeechWorkerTests.Check(WhisperNative.ValidateLayout(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"whisper-1.9.4-abi.json")),"EveryNativeFieldMatchesCompiledPinnedHeader");
  SpeechWorkerTests.Check(IntPtr.Size==8&&Marshal.SizeOf(typeof(WhisperNative.ContextParams))==48,"ContextIsWindowsX64Layout");
  using(var path=new OwnedContractDirectory()){
   string metadata=Path.Combine(path.Root,"abi.json");File.WriteAllText(metadata,"{\"version\":\"1.9.4\",\"headerSha256\":\"a7d19f7feb5be52426628ff07e0602de28a30dc4312d0d0603e1e536753f76dd\",\"pointerSize\":8,\"boolSize\":1,\"structures\":null}");
   bool rejected=false;try{rejected=!WhisperNative.ValidateLayout(metadata);}catch(Exception){}
   SpeechWorkerTests.Check(rejected,"MalformedAbiMetadataReturnsFalseWithoutNativeEntry");
   File.WriteAllText(metadata,File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"whisper-1.9.4-abi.json")).Replace("\"type\": \"bool8\"","\"type\": \"float32\""));
   SpeechWorkerTests.Check(!WhisperNative.ValidateLayout(metadata),"WrongNativeFieldTypeCannotPassLayoutCheck");
   string badWave=Path.Combine(path.Root,"bad.wav");File.WriteAllBytes(badWave,new byte[]{1,2,3});bool badRejected=false;
   try{SpeechWorkerProgram.ReadOwnedWave(path.Root,badWave);}catch(InvalidDataException){badRejected=true;}
   SpeechWorkerTests.Check(badRejected,"CorruptOwnedWaveCannotReachNativeDecoder");
   bool traversal=false;try{SpeechWorkerProgram.ReadOwnedWave(path.Root,Path.Combine(path.Root,"..","other.wav"));}catch(InvalidDataException){traversal=true;}
   SpeechWorkerTests.Check(traversal,"WavePathCannotEscapeRegisteredSessionRoot");
   string file=Path.Combine(path.Root,"library.dll");File.WriteAllBytes(file,new byte[]{1,2,3});string hash;
   using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(new byte[]{1,2,3})).Replace("-","").ToLowerInvariant();
   SpeechWorkerTests.Check(NativeLibraryLoader.HasExpectedHash(file,hash,3),"ExactBinaryAcceptedBeforeLoad");
   SpeechWorkerTests.Check(!NativeLibraryLoader.HasExpectedHash(file,new string('0',64),3),"WrongBinaryHashRejected");
   SpeechWorkerTests.Check(!NativeLibraryLoader.HasExpectedHash(file,hash,4),"WrongBinaryLengthRejected");
   SpeechWorkerTests.Check(!NativeLibraryLoader.HasExpectedHash(file+"missing",hash,3),"MissingBinaryRejected");
   bool missing=false;try{NativeLibraryLoader.Open(new SpeechRuntimePaths(path.Root));}catch(InvalidDataException){missing=true;}SpeechWorkerTests.Check(missing,"MissingDependenciesFailClosedBeforeNativeCall");
  }
  WhisperNative.AbortCall callback=data=>true;
  using(var lease=new WhisperNative.CallbackLease(callback)){
   var weak=new WeakReference(callback);callback=null;GC.Collect();GC.WaitForPendingFinalizers();
   SpeechWorkerTests.Check(weak.IsAlive&&lease.Pointer!=IntPtr.Zero,"AbortCallbackRemainsRootedUntilNativeCallEnds");
   SpeechWorkerTests.Check(((WhisperNative.AbortCall)Marshal.GetDelegateForFunctionPointer(lease.Pointer,typeof(WhisperNative.AbortCall)))(IntPtr.Zero),"AbortReturnValueIsNativeBool8");
  }
  SpeechWorkerTests.Check(!WhisperNative.IsExpectedVersion("1.9.3")&&!WhisperNative.IsExpectedVersion("1.9.4-modified")&&WhisperNative.IsExpectedVersion("1.9.4"),"NativeVersionMustMatchExactly");
  bool noExport=false;try{new NativeLibraryLoader().Export<WhisperNative.PointerCall>("missing.dll","missing_export");}catch(InvalidDataException){noExport=true;}SpeechWorkerTests.Check(noExport,"MissingNativeExportFailsClosed");
  using(var queue=new NativeDecodeQueue())using(var enter=new ManualResetEventSlim())using(var release=new ManualResetEventSlim()){
   int active=0,peak=0;var first=queue.Enqueue(()=>{int value=Interlocked.Increment(ref active);peak=value;enter.Set();if(!release.Wait(3000))throw new Exception("test_timeout");Interlocked.Decrement(ref active);},CancellationToken.None);
   SpeechWorkerTests.Check(enter.Wait(3000),"DecodeQueueStartsOneOwnedThread");
   var second=queue.Enqueue(()=>{int value=Interlocked.Increment(ref active);peak=Math.Max(peak,value);Interlocked.Decrement(ref active);},CancellationToken.None);
   SpeechWorkerTests.Check(!second.IsCompleted,"SecondDecodeCannotEnterConcurrentContext");release.Set();Task.WhenAll(first,second).GetAwaiter().GetResult();SpeechWorkerTests.Check(peak==1,"NativeDecodesNeverOverlap");
  }
  Console.WriteLine("PASS native contracts: compiled layout, exact hashes/version and serial execution");
 }
 internal static void RunRealDecodeContractTests(string ownedRoot){
  string root=Path.GetFullPath(ownedRoot),temporary=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;Guid owner;
  SpeechWorkerTests.Check(root.StartsWith(temporary,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(root).StartsWith("Yike-voice-",StringComparison.Ordinal)&&Guid.TryParseExact(Path.GetFileName(root).Substring(11),"N",out owner),"NativeContractAudioRootMustBeOwned");
  float[] samples=SpeechWorkerProgram.ReadOwnedWave(root,Path.Combine(root,"sample.wav"));
  using(var context=WhisperContext.Open(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory)))using(var cancel=new CancellationTokenSource()){
   SpeechWorkerTests.Check(context.SupportsLanguage("fr")&&!context.SupportsLanguage("zz")&&!context.SupportsLanguage("../fr"),"ExplicitLanguagesUsePinnedNativeValidationBeforeCapture");
   var first=Task.Run(()=>context.Decode(samples,"zh",false,false,cancel.Token));
   var decoding=typeof(WhisperContext).GetField("decoding",BindingFlags.Instance|BindingFlags.NonPublic);
   try{
    SpeechWorkerTests.Check(SpinWait.SpinUntil(()=>(bool)decoding.GetValue(context)||first.IsCompleted,3000)&&!first.IsCompleted,"NativeCallActuallyEntered");
    bool busy=false;try{context.Decode(samples,"zh",false,false,CancellationToken.None);}catch(InvalidOperationException ex){busy=ex.Message=="native_decode_concurrent";}
    SpeechWorkerTests.Check(busy,"SameContextConcurrentDecodeRejectedBeforeNativeEntry");
   }finally{cancel.Cancel();}
   bool stopped=false;try{first.GetAwaiter().GetResult();}catch(OperationCanceledException){stopped=true;}SpeechWorkerTests.Check(stopped,"ActualNativeAbortReturnsBeforeContextFree");
  }
  Console.WriteLine("PASS real native contracts: concurrent entry rejected, cancellation returned, context freed");
 }
 private sealed class OwnedContractDirectory:IDisposable {
  internal readonly string Root=Path.Combine(Path.GetTempPath(),"Yike-native-contract-"+Guid.NewGuid().ToString("N"));
  internal OwnedContractDirectory(){Directory.CreateDirectory(Root);}
  public void Dispose(){Directory.Delete(Root,true);}
 }
}
}
