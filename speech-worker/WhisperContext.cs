using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
namespace WindowsTranslator {
internal sealed class WhisperContext:IDisposable {
 internal const string ModelSha256="49c8fb02b65e6049d5fa6c04f81f53b867b5ec9540406812c643f177317f779f";
 private readonly WhisperNative api;private readonly NativeLibraryLoader loader;private readonly SpeechRuntimePaths paths;private readonly object lifetime=new object();private IntPtr context;private bool decoding,disposed;
 internal NativeLibraryLoader Libraries {get{return loader;}}
 internal bool SupportsLanguage(string language){if(!SpeechLanguage.IsCode(language))return false;if(language=="auto")return true;using(var value=new WhisperNative.Utf8(language))return api.LanguageId(value.Pointer)>=0;}
 private WhisperContext(WhisperNative api,NativeLibraryLoader loader,SpeechRuntimePaths paths,IntPtr context){this.api=api;this.loader=loader;this.paths=paths;this.context=context;}
 // CPU-only does not require disabling the pinned runtime's default attention optimization.
 internal static WhisperNative.ContextParams CpuParameters(WhisperNative.ContextParams parameters){parameters.use_gpu=0;parameters.dtw_token_timestamps=0;return parameters;}
 internal static WhisperContext Open(SpeechRuntimePaths paths){
  if(!NativeLibraryLoader.HasExpectedHash(paths.ModelPath,ModelSha256,264464607))throw new InvalidDataException("native_model_invalid");
  var loader=NativeLibraryLoader.Open(paths);var api=new WhisperNative(loader);var defaults=api.ContextDefaults();if(defaults==IntPtr.Zero)throw new InvalidDataException("native_defaults_failed");
  try{
   var parameters=CpuParameters((WhisperNative.ContextParams)Marshal.PtrToStructure(defaults,typeof(WhisperNative.ContextParams)));
   using(var model=new WhisperNative.Utf8(paths.ModelPath)){var context=api.Init(model.Pointer,parameters);if(context==IntPtr.Zero)throw new InvalidDataException("native_initialization_failed");return new WhisperContext(api,loader,paths,context);}
  }finally{api.FreeContextParams(defaults);}
 }
 internal SpeechRecognitionCandidate Decode(float[] samples,string language,bool live,bool useVad,CancellationToken token){
  token.ThrowIfCancellationRequested();if(samples==null||samples.Length==0||samples.Length>4800000)throw new InvalidDataException("native_samples_invalid");
  foreach(float sample in samples)if(float.IsNaN(sample)||float.IsInfinity(sample)||sample<-1||sample>1)throw new InvalidDataException("native_samples_invalid");
  if(!SupportsLanguage(language))throw new InvalidDataException("native_language_invalid");
  lock(lifetime){if(disposed)throw new ObjectDisposedException("WhisperContext");if(decoding)throw new InvalidOperationException("native_decode_concurrent");decoding=true;}
  try{
   if(useVad&&!NativeLibraryLoader.HasExpectedHash(paths.VadPath,WhisperVadConfiguration.Sha256,885098))return new SpeechRecognitionCandidate("",-1,"vad_model_invalid",null);
   IntPtr defaults=api.FullDefaults(1);if(defaults==IntPtr.Zero)throw new InvalidDataException("native_defaults_failed");
   try{
    var p=(WhisperNative.FullParams)Marshal.PtrToStructure(defaults,typeof(WhisperNative.FullParams));
    p.strategy=1;p.n_threads=Math.Max(2,Math.Min(8,Environment.ProcessorCount/2));p.n_max_text_ctx=0;p.offset_ms=0;p.duration_ms=0;
    p.translate=0;p.no_context=1;p.no_timestamps=1;p.single_segment=(byte)(live?1:0);p.print_special=p.print_progress=p.print_realtime=p.print_timestamps=0;
    p.token_timestamps=0;p.max_tokens=live?64:0;p.debug_mode=0;p.audio_ctx=0;p.tdrz_enable=0;p.initial_prompt=IntPtr.Zero;p.prompt_tokens=IntPtr.Zero;p.prompt_n_tokens=0;p.carry_initial_prompt=0;
    p.detect_language=0;p.suppress_blank=1;p.suppress_nst=1;p.greedy.best_of=live?3:8;p.beam_search.beam_size=live?3:8;
    p.new_segment_callback=p.progress_callback=p.encoder_begin_callback=p.logits_filter_callback=IntPtr.Zero;
    p.vad=(byte)(useVad?1:0);p.vad_params.threshold=0.5f;p.vad_params.min_speech_duration_ms=250;p.vad_params.min_silence_duration_ms=500;p.vad_params.speech_pad_ms=150;p.vad_params.samples_overlap=0.10f;
    WhisperNative.AbortCall abort=data=>token.IsCancellationRequested;
    using(var callback=new WhisperNative.CallbackLease(abort))using(var lang=new WhisperNative.Utf8(language))using(var vad=new WhisperNative.Utf8(paths.VadPath)){
     p.language=lang.Pointer;p.vad_model_path=useVad?vad.Pointer:IntPtr.Zero;p.abort_callback=callback.Pointer;p.abort_callback_user_data=IntPtr.Zero;
     token.ThrowIfCancellationRequested();int result=api.Full(context,p,samples,samples.Length);GC.KeepAlive(abort);GC.KeepAlive(samples);token.ThrowIfCancellationRequested();
     if(result!=0)return new SpeechRecognitionCandidate("",result,"native_decode_failed",null);
     var text=new StringBuilder();var probabilities=new List<double>();int segments=api.Segments(context),eot=api.EndOfTextToken(context);
     if(segments<0||segments>32768)throw new InvalidDataException("native_result_limit");
     for(int segment=0;segment<segments;segment++){
      text.Append(WhisperNative.Utf8String(api.Text(context,segment)));int count=api.Tokens(context,segment);if(count<0||count>32768)throw new InvalidDataException("native_result_limit");
      for(int i=0;i<count;i++){var item=api.Token(context,segment,i);if(item.id>=0&&item.id<eot)probabilities.Add(item.p);}
      if(text.Length>65536||probabilities.Count>32768)throw new InvalidDataException("native_result_limit");
     }
     return new SpeechRecognitionCandidate(text.ToString().Trim(),0,"",probabilities.ToArray());
    }
   }finally{api.FreeFullParams(defaults);}
  }finally{lock(lifetime)decoding=false;GC.KeepAlive(api);GC.KeepAlive(loader);}
 }
 public void Dispose(){lock(lifetime){if(disposed)return;if(decoding)throw new InvalidOperationException("native_dispose_during_decode");disposed=true;api.FreeContext(context);context=IntPtr.Zero;GC.KeepAlive(api);GC.KeepAlive(loader);}}
}
}
