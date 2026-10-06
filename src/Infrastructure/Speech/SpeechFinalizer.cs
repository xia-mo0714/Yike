using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
internal sealed class SpeechFinalizer {
 private readonly bool confidenceRetry;
 private readonly WhisperVadConfiguration vad;
 internal SpeechFinalizer(bool confidenceRetryEnabled=false):this(confidenceRetryEnabled,new WhisperVadConfiguration()){}
 internal SpeechFinalizer(bool confidenceRetryEnabled,WhisperVadConfiguration configuration){confidenceRetry=confidenceRetryEnabled;vad=configuration;}
 internal async Task<SpeechRefinementResult> RunAsync(string audioPath,string language,string liveText,SpeechDecodeBudget budget,ISpeechFinalDecoder decoder,CancellationToken token){
  if(budget==null)throw new ArgumentNullException("budget");if(decoder==null)throw new ArgumentNullException("decoder");
  token.ThrowIfCancellationRequested();string enhanced=null;AudioEnhancementReport report;
  bool enhance=WaveAudioEnhancer.TryEnhance(audioPath,out enhanced,out report);string audio=enhance?enhanced:audioPath;
  try{
   bool useVad=vad!=null&&vad.IsModelValid();string failure=useVad?"":"vad_model_unavailable";
   int before=budget.AttemptsUsed;
   SpeechRecognitionCandidate first=await Attempt(audio,language,useVad,budget,decoder,token).ConfigureAwait(false);
   if(!SpeechResultQuality.HasText(first))failure=Failure(first,useVad?"vad_no_speech":"full_audio_failed");
   bool timedOut=first.FailureCode=="timeout";
   bool retry=!timedOut&&(!SpeechResultQuality.HasText(first)||(confidenceRetry&&SpeechResultQuality.Evaluate(first)==SpeechQualityState.Low));
   SpeechRecognitionCandidate second=null;
   if(retry&&budget.RemainingMilliseconds>0&&budget.AttemptsUsed<2){
    second=await Attempt(audio,language,false,budget,decoder,token).ConfigureAwait(false);
    if(!SpeechResultQuality.HasText(second))failure=Failure(second,"full_audio_failed");
   }
   token.ThrowIfCancellationRequested();var selection=SpeechResultQuality.Select(first,second,liveText);
   bool selectedFirst=selection.Candidate!=null&&object.ReferenceEquals(selection.Candidate,first);
   return new SpeechRefinementResult(selection.Text,useVad&&selectedFirst,!useVad||budget.AttemptsUsed-before>1,failure,report){Quality=selection.Quality,NeedsReview=selection.NeedsReview,AttemptsUsed=budget.AttemptsUsed};
  }finally{if(enhanced!=null)try{File.Delete(enhanced);}catch(IOException){}catch(UnauthorizedAccessException){}}
 }
 private static string Failure(SpeechRecognitionCandidate candidate,string fallback){return candidate==null||string.IsNullOrEmpty(candidate.FailureCode)?fallback:candidate.FailureCode;}
 private static async Task<SpeechRecognitionCandidate> Attempt(string audio,string language,bool useVad,SpeechDecodeBudget budget,ISpeechFinalDecoder decoder,CancellationToken token){
  token.ThrowIfCancellationRequested();if(!budget.TryBeginAttempt())return new SpeechRecognitionCandidate("",-1,"timeout",null);
  using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token)){
   int remaining=budget.RemainingMilliseconds;if(remaining<=0)return new SpeechRecognitionCandidate("",-1,"timeout",null);
   deadline.CancelAfter(remaining);
   try{
    var result=await decoder.DecodeAsync(new SpeechDecodeRequest(audio,language,useVad,remaining),deadline.Token).ConfigureAwait(false);
    token.ThrowIfCancellationRequested();if(deadline.IsCancellationRequested)return new SpeechRecognitionCandidate("",-1,"timeout",null);
    return result??new SpeechRecognitionCandidate("",-1,"decoder_invalid_result",null);
   }catch(OperationCanceledException){token.ThrowIfCancellationRequested();return new SpeechRecognitionCandidate("",-1,"timeout",null);}
   catch(Exception){token.ThrowIfCancellationRequested();return new SpeechRecognitionCandidate("",-1,"decoder_failed",null);}
  }
 }
}
}
