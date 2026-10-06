using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsTranslator {
public static partial class Tests {
 internal static void RunSpeechResultQualityTests(List<string> lines) {
  Check(SpeechResultQuality.Evaluate(Candidate("hello",0.8,0.8))==SpeechQualityState.Acceptable,"NormalTokenProbabilityIsAccepted");
  Check(SpeechResultQuality.Evaluate(Candidate("hello",0.1,0.1))==SpeechQualityState.Low,"LowTokenProbabilityNeedsReview");
  foreach(double[] values in new[]{null,new double[0],new[]{double.NaN},new[]{double.PositiveInfinity},new[]{1.1},new[]{-0.1},new[]{0.8,double.NaN}})
   Check(SpeechResultQuality.Evaluate(new SpeechRecognitionCandidate("hello",0,"",values))==SpeechQualityState.Unknown,"InvalidOrMissingProbabilityIsUnknown");
  Check(SpeechResultQuality.Evaluate(Candidate("hello",0))==SpeechQualityState.Low,"ZeroProbabilityIsLowNotUnknown");
  Check(SpeechResultQuality.Evaluate(Candidate("hello",0.1,0.1,0.9,0.9,0.9))==SpeechQualityState.Acceptable,"ExactlyFortyPercentIsNotMoreThanForty");
  Check(SpeechResultQuality.Evaluate(Candidate("hello",0.19,0.19,0.19,0.99,0.99))==SpeechQualityState.Low,"MoreThanFortyPercentLowTokensAreRejected");
  foreach(string normal in new[]{"人人都知道","very very good","今天测试 voice input 效果很好","你好𠮷野家"})
   Check(SpeechResultQuality.Evaluate(Candidate(normal,0.8))==SpeechQualityState.Acceptable,"LegalRepeatAndMixedLanguageNotRejected");
  string repeated="这是一次足够长的明显重复片段这是一次足够长的明显重复片段这是一次足够长的明显重复片段";
  Check(SpeechResultQuality.Evaluate(Candidate(repeated,0.8))==SpeechQualityState.Low,"LongPhraseLoopNeedsReview");
  Check(SpeechResultQuality.Select(Candidate(repeated,0.8),null,"draft").NeedsReview,"RepetitionPreservesTextWithReview");
  var first=Candidate("first words",Math.Exp(-1.05),Math.Exp(-1.05),Math.Exp(-1.05),Math.Exp(-1.05));
  var improved=Candidate("better words",Math.Exp(-0.90),Math.Exp(-0.90),Math.Exp(-0.90),Math.Exp(-0.90));
  Check(SpeechResultQuality.Select(first,improved,"draft").Text=="better words","ImprovementAtPointFifteenReplacesFirst");
  Check(SpeechResultQuality.Select(first,Candidate("not enough",Math.Exp(-0.91),Math.Exp(-0.91),Math.Exp(-0.91),Math.Exp(-0.91)),"draft").Text=="first words","SmallImprovementDoesNotReplaceFirst");
  Check(SpeechResultQuality.Select(first,Candidate("too short",0.9),"draft").Text=="first words","RetryWithLessThanHalfTokensDoesNotReplaceFirst");
  Check(SpeechResultQuality.Select(first,Candidate("half retained",0.9,0.9),"draft").Text=="half retained","HalfTokenBoundaryCanReplaceFirst");
  Check(SpeechResultQuality.Select(first,Candidate("",0.9),"draft").Text=="first words","EmptyRetryNeverErasesFirst");
  Check(SpeechResultQuality.Select(Candidate("",0.9),Candidate("",0.9),"live draft").Text=="live draft","EmptyCandidatesNeverEraseLiveDraft");
  Check(SpeechResultQuality.Select(first,Candidate("still uncertain",0.1,0.1),"draft").NeedsReview&&!SpeechResultQuality.Select(first,null,"draft").AllowAutoSubmit,"AllLowCandidatesOnlyFillText");
  Check(SpeechResultQuality.Select(new SpeechRecognitionCandidate("known words",0,"",null),null,"").AllowAutoSubmit,"UnknownProbabilityKeepsNormalAutoSubmit");
  var mutable=new[]{0.8};var immutable=Candidate("immutable",mutable);mutable[0]=0.1;
  Check(SpeechResultQuality.Evaluate(immutable)==SpeechQualityState.Acceptable,"CandidateCopiesMutableNativeProbabilities");
  long clock=0;var budget=new SpeechDecodeBudget(TimeSpan.FromSeconds(1),()=>clock);
  Check(budget.RemainingMilliseconds==30000&&budget.TryBeginAttempt(),"ShortRecordingHasThirtySecondTotalBudget");
  clock=29999;Check(budget.RemainingMilliseconds==1,"RemainingBudgetUsesSameDeadline");
  clock=30000;Check(!budget.TryBeginAttempt()&&budget.AttemptsUsed==1,"ExpiredBudgetCannotRetry");
  clock=0;budget=new SpeechDecodeBudget(TimeSpan.FromSeconds(100),()=>clock);
  Check(budget.RemainingMilliseconds==300000,"LongRecordingBudgetCapsAtFiveMinutes");
  Check(budget.TryBeginAttempt()&&budget.TryBeginAttempt()&&!budget.TryBeginAttempt()&&budget.AttemptsUsed==2,"BudgetNeverAllowsThirdAttempt");
  clock=0;budget=new SpeechDecodeBudget(TimeSpan.FromSeconds(20),()=>clock);Check(budget.RemainingMilliseconds==80000,"DurationMultipliesByFour");
  RunBoundedFinalizerTests();
  lines.Add("PASS speech finalization: token quality, unknown statistics, repetition, safe candidate selection and shared two-attempt deadline");
 }
 private static SpeechRecognitionCandidate Candidate(string text,params double[] probabilities) {return new SpeechRecognitionCandidate(text,0,"",probabilities);}
 private sealed class ControlledDecoder:ISpeechFinalDecoder {
  internal readonly List<SpeechDecodeRequest> Requests=new List<SpeechDecodeRequest>();
  internal Func<int,SpeechDecodeRequest,CancellationToken,Task<SpeechRecognitionCandidate>> Run;
  public Task<SpeechRecognitionCandidate> DecodeAsync(SpeechDecodeRequest request,CancellationToken token){Requests.Add(request);return Run(Requests.Count,request,token);}
 }
 private static void RunBoundedFinalizerTests() {
  string directory=Path.Combine(Path.GetTempPath(),"Yike-voice-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  string path=Path.Combine(directory,"audio.wav");WriteTestWave(path,1,i=>0);
  try {
   long clock=0;var budget=new SpeechDecodeBudget(TimeSpan.FromSeconds(1),()=>clock);
   var decoder=new ControlledDecoder {Run=(number,request,ct)=>Task.FromResult(number==1?Candidate("uncertain",0.1,0.1):Candidate("clear words",0.9,0.9))};
   var result=new SpeechFinalizer(true).RunAsync(path,"en","draft",budget,decoder,CancellationToken.None).GetAwaiter().GetResult();
   Check(result.Text=="clear words"&&result.AttemptsUsed==2&&decoder.Requests[0].UseVad&&!decoder.Requests[1].UseVad,"LowConfidenceRetriesOnceWithoutVad");
   Check(decoder.Requests.TrueForAll(r=>r.Language=="en"&&r.TimeoutMilliseconds<=30000),"RetryKeepsLanguageAndSharedDeadline");
   decoder=new ControlledDecoder {Run=(number,request,ct)=>Task.FromResult(Candidate("same words",number==1?0.1:0.9,number==1?0.1:0.9))};
   result=new SpeechFinalizer(true).RunAsync(path,"en","draft",new SpeechDecodeBudget(TimeSpan.FromSeconds(1),()=>0),decoder,CancellationToken.None).GetAwaiter().GetResult();
   Check(result.Text=="same words"&&!result.UsedVad&&result.FellBack,"SameTextRetryReportsActualChosenDecode");
   decoder=new ControlledDecoder {Run=(number,request,ct)=>Task.FromResult(Candidate("uncertain",0.1,0.1))};budget=new SpeechDecodeBudget(TimeSpan.FromSeconds(1),()=>0);
   result=new SpeechFinalizer(false).RunAsync(path,"auto","draft",budget,decoder,CancellationToken.None).GetAwaiter().GetResult();
   Check(decoder.Requests.Count==1&&result.NeedsReview&&!result.AllowAutoSubmit,"DisabledQualityRetryKeepsDiagnosticSafety");
   decoder=new ControlledDecoder {Run=(number,request,ct)=>Task.FromResult(number==1?new SpeechRecognitionCandidate("",1,"native_crash",null):Candidate("legacy words"))};budget=new SpeechDecodeBudget(TimeSpan.FromSeconds(1),()=>0);
   result=new SpeechFinalizer(true).RunAsync(path,"zh","draft",budget,decoder,CancellationToken.None).GetAwaiter().GetResult();
   Check(decoder.Requests.Count==2&&result.Text=="legacy words"&&!budget.TryBeginAttempt(),"NativeFailureLeavesExactlyOneFallbackAttempt");
   decoder=new ControlledDecoder {Run=(number,request,ct)=>Task.FromResult(new SpeechRecognitionCandidate("",-1,"timeout",null))};
   result=new SpeechFinalizer(true).RunAsync(path,"en","draft",new SpeechDecodeBudget(TimeSpan.FromSeconds(1),()=>0),decoder,CancellationToken.None).GetAwaiter().GetResult();
   Check(decoder.Requests.Count==1&&result.Text=="draft"&&result.FailureCode=="timeout","TimeoutDoesNotRetryOrEraseDraft");
   clock=0;decoder=new ControlledDecoder {Run=(number,request,ct)=>{clock=30000;return Task.FromResult(Candidate("uncertain",0.1));}};
   result=new SpeechFinalizer(true).RunAsync(path,"en","draft",new SpeechDecodeBudget(TimeSpan.FromSeconds(1),()=>clock),decoder,CancellationToken.None).GetAwaiter().GetResult();
   Check(decoder.Requests.Count==1&&result.Text=="uncertain","DeadlineBetweenAttemptsPreservesFirst");
   using(var cancel=new CancellationTokenSource()) {
    decoder=new ControlledDecoder {Run=(number,request,ct)=>{cancel.Cancel();return Task.FromResult(Candidate("old words",0.1));}};
    bool cancelled=false;try{new SpeechFinalizer(true).RunAsync(path,"en","draft",new SpeechDecodeBudget(TimeSpan.FromSeconds(1),()=>0),decoder,cancel.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
    Check(cancelled&&decoder.Requests.Count==1,"CancellationNeverSpawnsRetry");
   }
   Check(Directory.GetFiles(directory).Length==1,"FinalizerRemovesOnlyOwnedEnhancedAudio");
   Check(SpeechRefinement.AudioDuration(path)==TimeSpan.FromSeconds(2),"WaveDurationUsesAudioSamplesNotFileOverhead");
   decoder=new ControlledDecoder {Run=(number,request,ct)=>Task.FromResult(new SpeechRecognitionCandidate("legacy words",0,"",null))};
   result=new SpeechFinalizer(false,new WhisperVadConfiguration(Path.Combine(directory,"missing.bin"))).RunAsync(path,"en","draft",new SpeechDecodeBudget(TimeSpan.FromSeconds(1),()=>0),decoder,CancellationToken.None).GetAwaiter().GetResult();
   Check(decoder.Requests.Count==1&&!decoder.Requests[0].UseVad&&result.FailureCode=="vad_model_unavailable"&&result.AllowAutoSubmit,"UnavailableVadUsesSingleFullDecodeAndUnknownQuality");
   WriteTestWave(path,1,i=>i/1600%5<3?0.05*Math.Sin(2*Math.PI*220*i/16000):0);
   string enhanced=null;
   decoder=new ControlledDecoder {Run=(number,request,ct)=>{enhanced=request.AudioPath;Check(enhanced!=path&&File.Exists(enhanced),"RealEnhancerFeedsOwnedAudio");return Task.FromResult(Candidate("words",0.8));}};
   result=new SpeechFinalizer(false).RunAsync(path,"en","draft",new SpeechDecodeBudget(TimeSpan.FromSeconds(1),()=>0),decoder,CancellationToken.None).GetAwaiter().GetResult();
   Check(result.EnhancementReport.EnhancementUsed&&enhanced!=null&&!File.Exists(enhanced)&&File.Exists(path),"RealEnhancedAudioIsRemovedButOriginalRetained");
  }finally{File.Delete(path);Directory.Delete(directory);}
 }
}
}
