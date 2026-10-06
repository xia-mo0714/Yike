using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
internal sealed class SpeechWorkerSession:IDisposable {
 private readonly object sync=new object(),lifecycle=new object();
 private readonly SdlCaptureSession capture;
 private readonly Func<float[],string,CancellationToken,SpeechRecognitionCandidate> decode;
 private PcmWindowBuffer window;private SessionWaveWriter wave;private Timer timer;
 private CancellationTokenSource liveCancellation;private Task liveCompletion=Task.FromResult(0);
 private readonly SpeechDraft draft=new SpeechDraft();private string text="",committedText="",language;private Guid id;
 private long samples,windowBase;private int captureGeneration;private bool running,accepting,decoding,limitQueued,disposed;
 internal event Action<SpeechWorkerMessage> EventReceived;
 internal Task Completion {get{lock(sync)return liveCompletion;}}
 internal bool IsCapturing {get{lock(sync)return running;}}
 internal SpeechWorkerPayload LastStopped {get;private set;}
 internal long CapturedSamples {get{lock(sync)return samples;}}
 internal SpeechWorkerSession(SdlCaptureSession capture,Func<float[],string,CancellationToken,SpeechRecognitionCandidate> decode){
  if(capture==null||decode==null)throw new ArgumentNullException();this.capture=capture;this.decode=decode;
  capture.SamplesReceived+=OnSamples;capture.Failed+=OnFailure;
 }
 internal void Start(Guid sessionId,string language,string device,string root){lock(lifecycle){
  if(disposed)throw new ObjectDisposedException("SpeechWorkerSession");
  if(sessionId==Guid.Empty||Path.GetFileName(Path.GetFullPath(root))!="Yike-voice-"+sessionId.ToString("N")||!SpeechLanguage.IsCode(language))throw new InvalidDataException("capture_session_arguments");
  lock(sync){if(running||!liveCompletion.IsCompleted)throw new InvalidOperationException("capture_session_busy");
   id=sessionId;this.language=language;window=new PcmWindowBuffer();wave=new SessionWaveWriter(root);samples=windowBase=0;limitQueued=false;decoding=false;text=committedText="";LastStopped=null;draft.Begin("",0,0);
   if(liveCancellation!=null)liveCancellation.Dispose();liveCancellation=new CancellationTokenSource();running=accepting=true;
  }
  var clock=System.Diagnostics.Stopwatch.StartNew();try{
   var endpoint=capture.Start(device);captureGeneration=capture.Generation;timer=new Timer(_=>Preview(),null,1000,1000);
   Emit("capture_started",new SpeechWorkerPayload{deviceName=endpoint.DeviceName,deviceIndex=endpoint.DeviceIndex,captureReadyMilliseconds=(int)clock.ElapsedMilliseconds});
  }catch{lock(sync){running=accepting=false;wave.Dispose();wave=null;}capture.Stop();throw;}
 }}
 private void OnSamples(float[] batch){bool stop=false;lock(sync){
  if(!accepting||batch==null)return;int count=(int)Math.Min(batch.Length,4800000-samples);if(count>0){if(count!=batch.Length){var clipped=new float[count];Array.Copy(batch,clipped,count);batch=clipped;}wave.Append(batch);window.Append(batch);samples+=count;}
  if(samples==4800000&&!limitQueued){limitQueued=true;stop=true;}
 }if(stop)ThreadPool.QueueUserWorkItem(_=>Stop("duration-limit"));}
 private void OnFailure(string code,int generation){lock(lifecycle){if(generation!=captureGeneration)return;Finish("capture-failure",code,false);}}
 internal void Preview(){float[] batch;long end;bool full;Guid generation;CancellationToken token;lock(sync){
  if(!running||decoding||samples-windowBase<16000)return;batch=window.TakeLatestWindow();if(batch==null)return;
  int count=(int)Math.Min(batch.Length,samples-windowBase);if(count<batch.Length){var segment=new float[count];Array.Copy(batch,batch.Length-count,segment,0,count);batch=segment;}
  end=samples;full=batch.Length==128000;generation=id;token=liveCancellation.Token;decoding=true;
  liveCompletion=Task.Run(()=>DecodeDraft(batch,end,full,generation,token));
 }}
 private void DecodeDraft(float[] batch,long end,bool full,Guid generation,CancellationToken token){try{
  var candidate=decode(batch,language,token);if(candidate==null||candidate.ExitCode!=0||string.IsNullOrWhiteSpace(candidate.Text))return;
  string result;lock(sync){if(!running||generation!=id||token.IsCancellationRequested)return;
   string incoming=SpeechText.NormalizeMixedLanguages(candidate.Text);string next=Merge(committedText,incoming);
   if(next.Length>65536)throw new InvalidDataException("capture_draft_limit");int caret;if(!draft.TryApply(text,next,false,out result,out caret))return;text=result;
   if(full){committedText=result;windowBase=end-8000;}
  }Emit("draft",new SpeechWorkerPayload{text=result});
 }catch(OperationCanceledException){}catch(Exception){/* A failed preview cannot discard captured audio. */}
 finally{lock(sync)decoding=false;}}
 internal static string Merge(string previous,string next){
  if(string.IsNullOrWhiteSpace(previous))return next;if(string.IsNullOrWhiteSpace(next))return previous;
  if(previous.EndsWith(next,StringComparison.OrdinalIgnoreCase))return previous;
  for(int length=Math.Min(previous.Length,next.Length);length>=2;length--){if(string.Compare(previous,previous.Length-length,next,0,length,StringComparison.OrdinalIgnoreCase)!=0)continue;
   if(length<next.Length&&char.IsLetterOrDigit(next[length-1])&&char.IsLetterOrDigit(next[length])&&next[length-1]<128&&next[length]<128)continue;
   return previous+SpeechText.Insertion(previous,next.Substring(length),"");
  }return previous+SpeechText.Insertion(previous,next,"");
 }
 internal void Stop(string reason){Finish(reason??"manual","",false);}
 internal void Cancel(){Finish("cancelled","",true);}
 private void Finish(string reason,string failure,bool cancelled){lock(lifecycle){
  Timer ownedTimer;lock(sync){if(!running)return;running=false;ownedTimer=timer;timer=null;liveCancellation.Cancel();}
  if(ownedTimer!=null)ownedTimer.Dispose();capture.Stop();SpeechWorkerPayload payload;
  lock(sync){accepting=false;string path=wave.Complete();wave=null;payload=new SpeechWorkerPayload{audioPath=path,text=text,stopReason=reason,failureCode=failure};LastStopped=payload;if(cancelled)draft.Cancel();}
  if(!cancelled)Emit("stopped",payload);
 }}
 private void Emit(string kind,SpeechWorkerPayload payload){var handler=EventReceived;if(handler!=null)handler(new SpeechWorkerMessage{Version=1,SessionId=id,Kind=kind,Payload=payload});}
 public void Dispose(){lock(lifecycle){if(disposed)return;Cancel();disposed=true;capture.SamplesReceived-=OnSamples;capture.Failed-=OnFailure;capture.Dispose();}Completion.GetAwaiter().GetResult();if(liveCancellation!=null)liveCancellation.Dispose();}
}
}
