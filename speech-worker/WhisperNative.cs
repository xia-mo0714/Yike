using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
namespace WindowsTranslator {
// Byte fields deliberately represent C bool; CLR's default bool marshaling is four bytes.
internal sealed class WhisperNative {
 [StructLayout(LayoutKind.Sequential,Pack=8)]internal struct AheadList {public UIntPtr n_heads;public IntPtr heads;}
 [StructLayout(LayoutKind.Sequential,Pack=8)]internal struct ContextParams {
  public byte use_gpu,flash_attn;public int gpu_device;public byte dtw_token_timestamps;public int dtw_aheads_preset,dtw_n_top;public AheadList dtw_aheads;public UIntPtr dtw_mem_size;
 }
 [StructLayout(LayoutKind.Sequential,Pack=8)]internal struct GreedyParams {public int best_of;}
 [StructLayout(LayoutKind.Sequential,Pack=8)]internal struct BeamParams {public int beam_size;public float patience;}
 [StructLayout(LayoutKind.Sequential,Pack=8)]internal struct VadParams {public float threshold;public int min_speech_duration_ms,min_silence_duration_ms;public float max_speech_duration_s;public int speech_pad_ms;public float samples_overlap;}
 [StructLayout(LayoutKind.Sequential,Pack=8)]internal struct FullParams {
  public int strategy,n_threads,n_max_text_ctx,offset_ms,duration_ms;
  public byte translate,no_context,no_timestamps,single_segment,print_special,print_progress,print_realtime,print_timestamps,token_timestamps;
  public float thold_pt,thold_ptsum;public int max_len;public byte split_on_word;public int max_tokens;public byte debug_mode;public int audio_ctx;public byte tdrz_enable;
  public IntPtr suppress_regex,initial_prompt;public byte carry_initial_prompt;public IntPtr prompt_tokens;public int prompt_n_tokens;public IntPtr language;
  public byte detect_language,suppress_blank,suppress_nst;public float temperature,max_initial_ts,length_penalty,temperature_inc,entropy_thold,logprob_thold,no_speech_thold;
  public GreedyParams greedy;public BeamParams beam_search;
  public IntPtr new_segment_callback,new_segment_callback_user_data,progress_callback,progress_callback_user_data,encoder_begin_callback,encoder_begin_callback_user_data;
  public IntPtr abort_callback,abort_callback_user_data,logits_filter_callback,logits_filter_callback_user_data,grammar_rules;
  public UIntPtr n_grammar_rules,i_start_rule;public float grammar_penalty;public byte vad;public IntPtr vad_model_path;public VadParams vad_params;
 }
 [StructLayout(LayoutKind.Sequential,Pack=8)]internal struct TokenData {public int id,tid;public float p,plog,pt,ptsum;public long t0,t1,t_dtw;public float vlen;}
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]internal delegate IntPtr PointerCall();
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]internal delegate IntPtr ParamsCall(int strategy);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]internal delegate IntPtr InitCall(IntPtr model,ContextParams parameters);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]internal delegate void FreeCall(IntPtr value);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]internal delegate int FullCall(IntPtr context,FullParams parameters,[In]float[] samples,int count);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]internal delegate int CountCall(IntPtr context);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]internal delegate int LanguageCall(IntPtr language);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]internal delegate int TokenCountCall(IntPtr context,int segment);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]internal delegate IntPtr TextCall(IntPtr context,int segment);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]internal delegate TokenData TokenCall(IntPtr context,int segment,int index);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)][return:MarshalAs(UnmanagedType.I1)]internal delegate bool AbortCall(IntPtr data);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]internal delegate void LogCall(int level,IntPtr text,IntPtr data);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]internal delegate void SetLogCall(LogCall callback,IntPtr data);
 internal readonly PointerCall ContextDefaults,Version;internal readonly ParamsCall FullDefaults;internal readonly InitCall Init;internal readonly FreeCall FreeContext,FreeContextParams,FreeFullParams;
 internal readonly FullCall Full;internal readonly CountCall Segments,EndOfTextToken;internal readonly TokenCountCall Tokens;internal readonly TextCall Text;internal readonly TokenCall Token;
 internal readonly LanguageCall LanguageId;
 private readonly LogCall quietLog;
 internal WhisperNative(NativeLibraryLoader loader){
  Version=loader.Export<PointerCall>("whisper.dll","whisper_version");if(!IsExpectedVersion(Utf8String(Version())))throw new InvalidDataException("native_version_mismatch");
  LanguageId=loader.Export<LanguageCall>("whisper.dll","whisper_lang_id");
  ContextDefaults=loader.Export<PointerCall>("whisper.dll","whisper_context_default_params_by_ref");FullDefaults=loader.Export<ParamsCall>("whisper.dll","whisper_full_default_params_by_ref");
  Init=loader.Export<InitCall>("whisper.dll","whisper_init_from_file_with_params");FreeContext=loader.Export<FreeCall>("whisper.dll","whisper_free");
  FreeContextParams=loader.Export<FreeCall>("whisper.dll","whisper_free_context_params");FreeFullParams=loader.Export<FreeCall>("whisper.dll","whisper_free_params");
  Full=loader.Export<FullCall>("whisper.dll","whisper_full");Segments=loader.Export<CountCall>("whisper.dll","whisper_full_n_segments");
  EndOfTextToken=loader.Export<CountCall>("whisper.dll","whisper_token_eot");Tokens=loader.Export<TokenCountCall>("whisper.dll","whisper_full_n_tokens");
  Text=loader.Export<TextCall>("whisper.dll","whisper_full_get_segment_text");Token=loader.Export<TokenCall>("whisper.dll","whisper_full_get_token_data");
  quietLog=(level,text,data)=>{};loader.Export<SetLogCall>("whisper.dll","whisper_log_set")(quietLog,IntPtr.Zero);
  loader.Export<SetLogCall>("ggml-base.dll","ggml_log_set")(quietLog,IntPtr.Zero);
 }
 internal static bool IsExpectedVersion(string version){return string.Equals(version,"1.9.4",StringComparison.Ordinal);}
 internal static string Utf8String(IntPtr value){if(value==IntPtr.Zero)return "";int count=0;while(count<1048576&&Marshal.ReadByte(value,count)!=0)count++;if(count==1048576)throw new InvalidDataException("native_text_limit");var bytes=new byte[count];Marshal.Copy(value,bytes,0,count);return new UTF8Encoding(false,true).GetString(bytes);}
 internal sealed class Utf8:IDisposable {
  internal IntPtr Pointer{get;private set;}
  internal Utf8(string text){var bytes=new UTF8Encoding(false,true).GetBytes((text??"")+"\0");Pointer=Marshal.AllocHGlobal(bytes.Length);Marshal.Copy(bytes,0,Pointer,bytes.Length);}
  public void Dispose(){if(Pointer!=IntPtr.Zero){Marshal.FreeHGlobal(Pointer);Pointer=IntPtr.Zero;}}
 }
 internal sealed class CallbackLease:IDisposable {
  private GCHandle root;internal IntPtr Pointer{get;private set;}
  internal CallbackLease(Delegate callback){root=GCHandle.Alloc(callback);Pointer=Marshal.GetFunctionPointerForDelegate(callback);}
  public void Dispose(){if(root.IsAllocated)root.Free();Pointer=IntPtr.Zero;}
 }
 internal static bool ValidateLayout(string path){
  try{
   var file=new FileInfo(path);if(!file.Exists||file.Length>131072||IntPtr.Size!=8)return false;
   var json=new JavaScriptSerializer{MaxJsonLength=131072,RecursionLimit=16}.DeserializeObject(File.ReadAllText(path)) as Dictionary<string,object>;
   if(json==null||(string)json["version"]!="1.9.4"||(string)json["headerSha256"]!="a7d19f7feb5be52426628ff07e0602de28a30dc4312d0d0603e1e536753f76dd"||Convert.ToInt32(json["pointerSize"])!=8||Convert.ToInt32(json["boolSize"])!=1)return false;
   var structures=json["structures"] as Dictionary<string,object>;if(structures==null)return false;
   foreach(var type in new[]{typeof(ContextParams),typeof(FullParams),typeof(VadParams),typeof(TokenData)}){
    var layout=structures[type.Name] as Dictionary<string,object>;if(layout==null||Marshal.SizeOf(type)!=Convert.ToInt32(layout["size"]))return false;
    var fields=layout["fields"] as Dictionary<string,object>;if(fields==null||fields.Count!=type.GetFields().Length)return false;
    foreach(var field in type.GetFields()){
     var expected=fields[field.Name] as Dictionary<string,object>;if(expected==null||Marshal.OffsetOf(type,field.Name).ToInt64()!=Convert.ToInt64(expected["offset"])||Marshal.SizeOf(field.FieldType)!=Convert.ToInt32(expected["size"])||!string.Equals((string)expected["type"],FieldType(field.FieldType),StringComparison.Ordinal))return false;
    }
   }
   return Marshal.SizeOf(typeof(ContextParams))==48&&Marshal.SizeOf(typeof(FullParams))==304&&Marshal.SizeOf(typeof(TokenData))==56;
  }catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}catch(KeyNotFoundException){return false;}catch(InvalidCastException){return false;}catch(InvalidOperationException){return false;}catch(FormatException){return false;}catch(OverflowException){return false;}
 }
 private static string FieldType(Type type){if(type==typeof(byte))return "bool8";if(type==typeof(int))return "int32";if(type==typeof(float))return "float32";if(type==typeof(long))return "int64";if(type==typeof(UIntPtr))return "size_t64";if(type==typeof(IntPtr))return "pointer64";return type.Name;}
}
}
