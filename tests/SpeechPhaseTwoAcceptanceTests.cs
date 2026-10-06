using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
public static partial class Tests {
 private static void RunSpeechPhaseTwoAcceptanceTests(List<string> lines){
  var timings=Enumerable.Range(0,11).Select(i=>new PhaseTwoCaptureRecord{index=i,warm=i>0,kind="capture",readyMilliseconds=i==0?100:50,success=true}).ToArray();
  Check(PhaseTwoAcceptance.CaptureGate(timings,11,100),"WarmCaptureExactlyHalfBaselineIsAccepted");
  Check(!PhaseTwoAcceptance.CaptureGate(timings.Take(10).ToArray(),11,100),"RemovingSlowOrFailedCaptureCannotPass");
  timings[1].success=false;Check(!PhaseTwoAcceptance.CaptureGate(timings,11,100),"FailedCaptureMustRemainAndFailGate");timings[1].success=true;
  timings[1].kind="preheat";Check(!PhaseTwoAcceptance.CaptureGate(timings,11,100),"PreheatIsNotCaptureReadiness");timings[1].kind="capture";
  Check(!PhaseTwoAcceptance.CaptureGate(timings,11,99),"WarmCaptureOverHalfFails");
  Check(PhaseTwoAcceptance.FinalGate(110,100)&&!PhaseTwoAcceptance.FinalGate(110.01,100),"FinalLatencyTenPercentBoundary");
  Check(PhaseTwoAcceptance.ReleasedMemoryGate(33554432)&&!PhaseTwoAcceptance.ReleasedMemoryGate(33554433),"ReleasedPrivateMemoryBoundary");
  Check(!PhaseTwoAcceptance.NoiseGate(.1,.1)&&PhaseTwoAcceptance.NoiseGate(.099,.1),"NoNoiseImprovementCannotPass");
  var baseline=new PhaseTwoQualityCase{id="one",language="zh",expected="frozen reference",audioSha256="audio",modelSha256="model",scorerSha256="scorer"};
  var candidate=new PhaseTwoQualityCase{id="one",language="zh",expected="frozen reference",audioSha256="audio",modelSha256="model",scorerSha256="scorer"};
  Check(PhaseTwoAcceptance.SameFixture(baseline,candidate),"FrozenIdentityAccepted");
  foreach(string field in new[]{"expected","audioSha256","modelSha256","scorerSha256"}){typeof(PhaseTwoQualityCase).GetField(field).SetValue(candidate,"changed");Check(!PhaseTwoAcceptance.SameFixture(baseline,candidate),"ChangedReferenceAudioModelScorerRejected");typeof(PhaseTwoQualityCase).GetField(field).SetValue(candidate,typeof(PhaseTwoQualityCase).GetField(field).GetValue(baseline));}
  string publicReport=Store.Json.Serialize(PhaseTwoAcceptance.PublicReport("Fail",new[]{"quality_not_improved"}));Check(!publicReport.Contains("expected")&&!publicReport.Contains("actual")&&!publicReport.Contains("probabilities")&&!publicReport.Contains("audioPath"),"PublicAcceptanceReportContainsNoPrivateContent");
  Check(Store.Json.Serialize(PhaseTwoAcceptance.ReportMedian(new double[0]))=="null","MissingWarmMeasurementsAreJsonNullNotNaN");
  string fixtureRoot=Path.Combine(Path.GetTempPath(),"Yike-phase2-gate-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixtureRoot);
  try {
   var oldRows=Enumerable.Range(0,15).Select(i=>new PhaseTwoQualityCase{id="case"+i,language="zh",culture="zh-CN",variant="fan-noise",expected="literal",audioSha256="audio",modelSha256="model",runtimeSha256="cli",elapsedMilliseconds=100,cer=.1,wer=.1,firstTokenRetained=true,lastTokenRetained=true}).ToArray();
   var newRows=oldRows.Select(x=>new PhaseTwoQualityCase{id=x.id,language=x.language,culture=x.culture,variant=x.variant,expected=x.expected,audioSha256=x.audioSha256,modelSha256=x.modelSha256,runtimeSha256="dll",elapsedMilliseconds=100,cer=.05,wer=.05,firstTokenRetained=true,lastTokenRetained=true}).ToArray();
   string oldFile=Path.Combine(fixtureRoot,"old.json"),newFile=Path.Combine(fixtureRoot,"new.json");
   File.WriteAllText(oldFile,Store.Json.Serialize(new PhaseTwoQualityReport{complete=true,pipeline="phase2",scorerSha256="changed",cases=oldRows}));File.WriteAllText(newFile,Store.Json.Serialize(new PhaseTwoQualityReport{complete=true,pipeline="phase2",scorerSha256="scorer",cases=newRows}));
   Check(Store.Json.Serialize(PhaseTwoAcceptance.CompareQuality(oldFile,newFile,"scorer")).Contains("frozen_scorer_mismatch"),"ComparisonCannotOverwriteChangedBaselineScorer");
  } finally {Directory.Delete(fixtureRoot,true);}
  lines.Add("PASS phase2 acceptance: immutable fixtures, complete capture attempts, exact latency/memory limits, strict noise improvement and content-free public report");
 }
}
internal sealed class PhaseTwoCaptureRecord {public int index;public bool warm,success;public string kind,failure;public double readyMilliseconds,draftMilliseconds;}
internal sealed class PhaseTwoQualityCase {
 public string id,language,variant,culture,expected,actual,audioSha256,modelSha256,runtimeSha256,scorerSha256,failure;
 public bool empty,firstTokenRetained,lastTokenRetained;public double cer,wer;public long elapsedMilliseconds;public int exitCode,attempts;public string quality;
}
internal sealed class PhaseTwoQualityReport {public bool complete;public string pipeline,scorerSha256;public PhaseTwoQualityCase[] cases;}
internal static class PhaseTwoAcceptance {
 internal static string VerifyScorer(string path){string source=File.ReadAllText(path).Replace("\r\n","\n");int begin=source.IndexOf("internal static class SpeechQualityMetrics",StringComparison.Ordinal),end=source.IndexOf("internal static class SpeechQualityTests",StringComparison.Ordinal);if(begin<0||end<begin)throw new InvalidDataException("Scorer source missing");using(var sha=System.Security.Cryptography.SHA256.Create()){string actual=BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(source.Substring(begin,end-begin)))).Replace("-","").ToLowerInvariant();if(actual!="6341f325e26425257d26dfa3d6efe876c5c10823f17e58e12a6bedfdbc668a3c")throw new InvalidDataException("Frozen scorer changed");return actual;}}
 internal static double Median(IEnumerable<double> values){var sorted=values.OrderBy(x=>x).ToArray();if(sorted.Length==0)return double.NaN;return sorted.Length%2==1?sorted[sorted.Length/2]:(sorted[sorted.Length/2-1]+sorted[sorted.Length/2])/2;}
 internal static double? ReportMedian(IEnumerable<double> values){double median=Median(values);return double.IsNaN(median)||double.IsInfinity(median)?(double?)null:median;}
 internal static bool CaptureGate(PhaseTwoCaptureRecord[] rows,int planned,double cold){return rows!=null&&rows.Length==planned&&rows.Select(x=>x.index).OrderBy(x=>x).SequenceEqual(Enumerable.Range(0,planned))&&rows.All(x=>x.success&&x.kind=="capture"&&x.readyMilliseconds>0&&string.IsNullOrEmpty(x.failure))&&rows.Count(x=>x.warm)>=10&&cold>0&&Median(rows.Where(x=>x.warm).Select(x=>x.readyMilliseconds))<=cold*.5;}
 internal static bool FinalGate(double candidate,double baseline){return candidate>0&&baseline>0&&candidate<=baseline*1.1;}
 internal static bool ReleasedMemoryGate(long delta){return delta<=33554432;}
 internal static bool NoiseGate(double candidate,double baseline){return candidate>=0&&baseline>candidate;}
 internal static bool SameFixture(PhaseTwoQualityCase baseline,PhaseTwoQualityCase candidate){return baseline!=null&&candidate!=null&&new[]{baseline.id,baseline.language,baseline.expected,baseline.audioSha256,baseline.modelSha256,baseline.scorerSha256}.All(x=>!string.IsNullOrEmpty(x))&&baseline.id==candidate.id&&baseline.language==candidate.language&&baseline.expected==candidate.expected&&baseline.audioSha256==candidate.audioSha256&&baseline.modelSha256==candidate.modelSha256&&baseline.scorerSha256==candidate.scorerSha256;}
 internal static object PublicReport(string status,string[] failures){return new{status=status,failures=failures};}
 internal static object CompareQuality(string baselinePath,string candidatePath,string scorer){
  var baseline=Store.Json.Deserialize<PhaseTwoQualityReport>(File.ReadAllText(baselinePath));var candidate=Store.Json.Deserialize<PhaseTwoQualityReport>(File.ReadAllText(candidatePath));var failures=new List<string>();
  if(!baseline.complete||!candidate.complete||baseline.cases==null||candidate.cases==null||baseline.cases.Length!=15||candidate.cases.Length!=15)return PublicReport("Unverified",new[]{"quality_records_incomplete"});
  if(candidate.scorerSha256!=scorer||(baseline.pipeline=="phase2"&&baseline.scorerSha256!=scorer))return PublicReport("Fail",new[]{"frozen_scorer_mismatch"});
  // The phase-one report predates scorer metadata. The caller binds its frozen
  // report hash and the unchanged scoring-source hash before this comparison.
  foreach(var row in baseline.cases)row.scorerSha256=scorer;
  foreach(var row in candidate.cases)row.scorerSha256=candidate.scorerSha256;
  if(baseline.cases.Select(x=>x.id+"/"+x.language).Distinct().Count()!=15||candidate.cases.Select(x=>x.id+"/"+x.language).Distinct().Count()!=15)return PublicReport("Fail",new[]{"duplicate_quality_records"});
  foreach(var old in baseline.cases){var current=candidate.cases.SingleOrDefault(x=>x.id==old.id&&x.language==old.language);if(!SameFixture(old,current)||old.culture!=current.culture||old.variant!=current.variant)return PublicReport("Fail",new[]{"frozen_fixture_mismatch"});if(current.exitCode!=0||!string.IsNullOrEmpty(current.failure)||current.elapsedMilliseconds<=0)failures.Add("quality_execution_failed");if(!old.empty&&current.empty||old.firstTokenRetained&&!current.firstTokenRetained||old.lastTokenRetained&&!current.lastTokenRetained)failures.Add("added_empty_or_truncation");}
  foreach(var group in baseline.cases.GroupBy(x=>x.culture+"/"+x.language)){var selected=candidate.cases.Where(x=>x.culture+"/"+x.language==group.Key);bool english=group.First().culture=="en-US";if(selected.Average(x=>english?x.wer:x.cer)>group.Average(x=>english?x.wer:x.cer)+1e-12)failures.Add("language_group_regression");}
  double oldNoise=Median(baseline.cases.Where(x=>x.variant=="low-volume"||x.variant=="fan-noise").Select(x=>x.cer)),newNoise=Median(candidate.cases.Where(x=>x.variant=="low-volume"||x.variant=="fan-noise").Select(x=>x.cer));
  if(!NoiseGate(newNoise,oldNoise))failures.Add("noise_not_improved");bool final=FinalGate(Median(candidate.cases.Select(x=>(double)x.elapsedMilliseconds)),Median(baseline.cases.Select(x=>(double)x.elapsedMilliseconds)));
  return new{status=failures.Count==0?"Pass":"Fail",failures=failures.Distinct().ToArray(),baselineMeanCer=baseline.cases.Average(x=>x.cer),candidateMeanCer=candidate.cases.Average(x=>x.cer),baselineNoiseMedianCer=oldNoise,candidateNoiseMedianCer=newNoise,finalLatency=final?"Pass":"Fail",baselineFinalMedianMilliseconds=Median(baseline.cases.Select(x=>(double)x.elapsedMilliseconds)),candidateFinalMedianMilliseconds=Median(candidate.cases.Select(x=>(double)x.elapsedMilliseconds)),cliSha256=baseline.cases.Select(x=>x.runtimeSha256).Distinct().ToArray(),dllSha256=candidate.cases.Select(x=>x.runtimeSha256).Distinct().ToArray(),scorerSha256=scorer};
 }
}
internal sealed class PhaseTwoFileSession:IDisposable {
 internal readonly Guid Id=Guid.NewGuid();internal readonly string DirectoryPath;internal readonly SpeechWorkerManager Manager;
 internal PhaseTwoFileSession(){DirectoryPath=Path.Combine(Path.GetTempPath(),"Yike-voice-"+Id.ToString("N"));Directory.CreateDirectory(DirectoryPath);Manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),null,()=>new OwnedSpeechWorkerProcess(p=>{p.StartInfo.Arguments+=" --file-test-root \""+DirectoryPath+"\"";return p.Start();}));Manager.RegisterOwnedDirectory(Id,DirectoryPath);}
 internal SpeechRefinementResult Decode(string frozenAudio,string language,bool retry){
  Manager.EnsureReadyAsync(CancellationToken.None).GetAwaiter().GetResult();
  // File QA uses the real manager/decoder without opening a microphone. State
  // priming lives only in this explicit diagnostic utility, never production.
  typeof(SpeechWorkerManager).GetField("activeSession",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(Manager,Id);
  string wave=Path.Combine(DirectoryPath,"recording.wav");File.Copy(frozenAudio,wave,true);var clock=Stopwatch.StartNew();
  try{return new SpeechFinalizer(retry).RunAsync(wave,language,"",new SpeechDecodeBudget(SpeechRefinement.AudioDuration(wave),()=>clock.ElapsedMilliseconds),new WorkerSpeechDecoder(Manager,Id),CancellationToken.None).GetAwaiter().GetResult();}
  finally{Manager.CompleteSession(Id);}
 }
 public void Dispose(){Manager.Dispose();}
}
internal static class PhaseTwoSpeechQa {
 private sealed class OwnedIdentity {internal int id;internal DateTime started;}
 private sealed class ResourceSample {public long elapsedMilliseconds,privateBytes,workingSetBytes,helperCpuTicks;public bool busy;public int ownedHelpers,handles,ownedTemporaryDirectories;}
 private static ResourceSample Snapshot(List<OwnedIdentity> owned,HashSet<string> roots,long elapsed,bool busy){long memory=0,working=0,cpu=0;int handles=0,count=0;OwnedIdentity[] identities;lock(owned)identities=owned.ToArray();foreach(var identity in identities)try{using(var process=Process.GetProcessById(identity.id)){if(process.HasExited||process.StartTime!=identity.started)continue;process.Refresh();count++;memory+=process.PrivateMemorySize64;working+=process.WorkingSet64;handles+=process.HandleCount;cpu+=process.TotalProcessorTime.Ticks;}}catch(ArgumentException){}catch(InvalidOperationException){}using(var app=Process.GetCurrentProcess()){app.Refresh();memory+=app.PrivateMemorySize64;working+=app.WorkingSet64;handles+=app.HandleCount;}
  int directories;lock(roots)directories=roots.Count(Directory.Exists);
  return new ResourceSample{elapsedMilliseconds=elapsed,busy=busy,ownedHelpers=count,privateBytes=memory,workingSetBytes=working,handles=handles,helperCpuTicks=cpu,ownedTemporaryDirectories=directories};
 }
 private static bool OwnedExited(List<OwnedIdentity> owned){OwnedIdentity[] identities;lock(owned)identities=owned.ToArray();foreach(var identity in identities)try{using(var process=Process.GetProcessById(identity.id))if(!process.HasExited&&process.StartTime==identity.started)return false;}catch(ArgumentException){}return true;}
 private static SpeechWorkerManager MakeManager(List<OwnedIdentity> owned){return new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),null,()=>new OwnedSpeechWorkerProcess(p=>{bool result=p.Start();if(result)lock(owned)owned.Add(new OwnedIdentity{id=p.Id,started=p.StartTime});return result;}));}
 internal static void Run(string mode,string output){
  if(mode=="native"){using(var session=new PhaseTwoFileSession()){string fixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","work","phase1-quality","fixtures","zh-normal.wav");var result=session.Decode(fixture,"zh",false);File.WriteAllText(output,Store.Json.Serialize(new{mode=mode,complete=true,status=string.IsNullOrWhiteSpace(result.Text)?"Fail":"Pass",attempts=result.AttemptsUsed,quality=result.Quality.ToString(),failure=result.FailureCode}));}return;}
  if(mode=="latency"){RunLatency(output);return;}
  if(mode!="cycles"&&mode!="soak")throw new ArgumentException("Unknown QA mode");
  var owned=new List<OwnedIdentity>();var clock=Stopwatch.StartNew();var samples=new List<ResourceSample>();var cycles=new List<object>();var roots=new HashSet<string>();var failures=new List<string>();int busy=0,samplingFailed=0;
  GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long initial;using(var current=Process.GetCurrentProcess()){current.Refresh();initial=current.PrivateMemorySize64;}
  int planned=30;long duration=mode=="soak"?1800000:0;long nextSample=0,nextCycle=0;
  // Sampling is independent of slow model loads and finalization; it is not
  // paused while a session waits on native execution.
  using(var sampler=new Timer(_=>{try{var sample=Snapshot(owned,roots,clock.ElapsedMilliseconds,Volatile.Read(ref busy)!=0);lock(samples)samples.Add(sample);}catch(Exception){Interlocked.Exchange(ref samplingFailed,1);}},null,0,5000)){
  using(var manager=MakeManager(owned)){
   for(int cycle=0;cycle<planned;cycle++){
    while(clock.ElapsedMilliseconds<nextCycle){if(clock.ElapsedMilliseconds>=nextSample){nextSample=clock.ElapsedMilliseconds+5000;lock(samples)File.WriteAllText(output,Store.Json.Serialize(new{mode=mode,complete=false,plannedCycles=planned,durationMilliseconds=clock.ElapsedMilliseconds,cycles=cycles,samples=samples}));}Thread.Sleep(100);}
    Volatile.Write(ref busy,1);long begun=clock.ElapsedMilliseconds;string failure="";bool cancelled=cycle%3!=0&&cycle!=planned-1,exited=true;
    using(var backend=new ResidentSpeechInput(manager)){
     string error;backend.Failed+=message=>failure="capture_or_finalization_failed";
     if(!backend.Start("auto",out error))failure="capture_start_failed";
     else {lock(roots)roots.Add((string)typeof(ResidentSpeechInput).GetField("directory",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(backend));if(!SpinWait.SpinUntil(()=>!string.IsNullOrEmpty(backend.DeviceName)||!backend.IsListening,25000)||string.IsNullOrEmpty(backend.DeviceName)||manager.Status.State!=SpeechWorkerState.Ready)failure="capture_not_ready";}
     if(string.IsNullOrEmpty(failure)){Thread.Sleep(mode=="soak"?300:30);if(cancelled)backend.Dispose();else backend.Stop();if(!backend.Completion.Wait(35000))failure="capture_completion_timeout";}
    }
    if(cancelled){exited=SpinWait.SpinUntil(()=>OwnedExited(owned),3000);if(!exited)failure="cancel_exit_over_three_seconds";}
    Volatile.Write(ref busy,0);if(failure.Length>0)failures.Add(failure);cycles.Add(new{index=cycle,cancelled=cancelled,elapsedMilliseconds=clock.ElapsedMilliseconds-begun,failure=failure,cancelOwnedExit=exited});nextSample=clock.ElapsedMilliseconds+5000;nextCycle=mode=="soak"?(cycle+1)*60000:clock.ElapsedMilliseconds;
    lock(samples)File.WriteAllText(output,Store.Json.Serialize(new{mode=mode,complete=false,plannedCycles=planned,durationMilliseconds=clock.ElapsedMilliseconds,cycles=cycles,samples=samples}));
   }
   while(clock.ElapsedMilliseconds<duration){if(clock.ElapsedMilliseconds>=nextSample){nextSample=clock.ElapsedMilliseconds+5000;lock(samples)File.WriteAllText(output,Store.Json.Serialize(new{mode=mode,complete=false,plannedCycles=planned,durationMilliseconds=clock.ElapsedMilliseconds,cycles=cycles,samples=samples}));}Thread.Sleep(100);}
   // Normal completion retains the model until the real 120-second deadline.
   var idle=Stopwatch.StartNew();while(!OwnedExited(owned)&&idle.ElapsedMilliseconds<125000)Thread.Sleep(100);
   if(!OwnedExited(owned))failures.Add("idle_release_failed");
  }
  using(var callbacksEnded=new ManualResetEvent(false)){sampler.Dispose(callbacksEnded);if(!callbacksEnded.WaitOne(3000))failures.Add("resource_sampler_exit_timeout");}
  }
  GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long released;using(var current=Process.GetCurrentProcess()){current.Refresh();released=current.PrivateMemorySize64;}
  long delta=released-initial;if(!PhaseTwoAcceptance.ReleasedMemoryGate(delta))failures.Add("released_private_delta_over_32mib");
  int remaining;lock(roots)remaining=roots.Count(Directory.Exists);if(remaining!=0)failures.Add("owned_audio_not_cleaned");if(samplingFailed!=0||samples.Count==0)failures.Add("resource_sampling_failed");if(samples.Any(x=>x.ownedHelpers>1))failures.Add("multiple_owned_helpers");
  var idleCpu=new List<double>();for(int i=1;i<samples.Count;i++){var previous=samples[i-1];var current=samples[i];if(!previous.busy&&!current.busy&&previous.ownedHelpers==1&&current.ownedHelpers==1&&current.helperCpuTicks>=previous.helperCpuTicks&&current.elapsedMilliseconds>previous.elapsedMilliseconds)idleCpu.Add((current.helperCpuTicks-previous.helperCpuTicks)/10000.0/(current.elapsedMilliseconds-previous.elapsedMilliseconds)/Environment.ProcessorCount*100);}
  File.WriteAllText(output,Store.Json.Serialize(new{mode=mode,complete=true,status=failures.Count==0?"Pass":"Fail",failures=failures.Distinct().ToArray(),plannedCycles=planned,durationMilliseconds=clock.ElapsedMilliseconds,initialPrivateBytes=initial,releasedPrivateBytes=released,releasedPrivateDelta=delta,ownedHelpersAfterRelease=OwnedExited(owned)?0:1,ownedTemporaryDirectoriesAfterRelease=remaining,idleCpuMedianPercent=idleCpu.Count>0?(double?)PhaseTwoAcceptance.Median(idleCpu):null,cycles=cycles,samples=samples,hardwareSwitch="Unverified",privateVideo="Unverified"}));
 }
 private static void RunLatency(string output){
  var baseline=new List<PhaseTwoCaptureRecord>();var candidate=new List<PhaseTwoCaptureRecord>();
  for(int i=0;i<10;i++){var clock=Stopwatch.StartNew();using(var legacy=new WhisperSpeechInput()){string error,failure="";legacy.Failed+=message=>failure="legacy_capture_failed";bool start=legacy.Start("auto",out error);bool ready=start&&SpinWait.SpinUntil(()=>legacy.IsReady||!legacy.IsListening,25000)&&legacy.IsReady;double ms=clock.Elapsed.TotalMilliseconds;legacy.Dispose();legacy.Completion.Wait(5000);baseline.Add(new PhaseTwoCaptureRecord{index=i,kind="capture",warm=false,success=ready&&failure.Length==0,readyMilliseconds=ms,draftMilliseconds=-1,failure=ready?failure:"legacy_capture_not_ready"});}}
  var owned=new List<OwnedIdentity>();using(var manager=MakeManager(owned)){for(int i=0;i<11;i++){var clock=Stopwatch.StartNew();using(var backend=new ResidentSpeechInput(manager)){string error,failure="";backend.Failed+=message=>failure="native_capture_failed";bool start=backend.Start("auto",out error);bool ready=start&&SpinWait.SpinUntil(()=>!string.IsNullOrEmpty(backend.DeviceName)||!backend.IsListening,25000)&&!string.IsNullOrEmpty(backend.DeviceName)&&manager.Status.State==SpeechWorkerState.Ready;double ms=clock.Elapsed.TotalMilliseconds;bool warm=manager.Status.IsWarm;if(ready){Thread.Sleep(30);backend.Stop();if(!backend.Completion.Wait(35000))failure="native_finalization_timeout";}candidate.Add(new PhaseTwoCaptureRecord{index=i,kind="capture",warm=warm,success=ready&&failure.Length==0,readyMilliseconds=ms,draftMilliseconds=-1,failure=ready?failure:"native_capture_not_ready"});}}}
  double cold=PhaseTwoAcceptance.Median(baseline.Select(x=>x.readyMilliseconds));bool pass=baseline.Count==10&&baseline.All(x=>x.success)&&PhaseTwoAcceptance.CaptureGate(candidate.ToArray(),11,cold);
  File.WriteAllText(output,Store.Json.Serialize(new{mode="latency",complete=true,status=pass?"Pass":"Fail",plannedBaseline=10,plannedCandidate=11,baseline=baseline,candidate=candidate,baselineColdMedianMilliseconds=cold,candidateWarmMedianMilliseconds=PhaseTwoAcceptance.ReportMedian(candidate.Where(x=>x.warm).Select(x=>x.readyMilliseconds)),firstDraft="Unverified",firstDraftReason="No controlled spoken reference was played into the physical microphone"}));
 }
}
}
