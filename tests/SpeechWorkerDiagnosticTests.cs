using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
namespace WindowsTranslator {
public static partial class Tests {
 private static void RunSpeechWorkerDiagnosticTests(List<string> lines){
  var json=new JavaScriptSerializer();Check(!json.Deserialize<Preferences>("{}").PreheatVoiceModel&&!new Preferences().PreheatVoiceModel,"PreheatDefaultsOffForExistingUsers");var prefs=new Preferences{PreheatVoiceModel=true};Check(json.Deserialize<Preferences>(json.Serialize(prefs)).PreheatVoiceModel,"PreheatChoiceRoundTrips");
  foreach(var state in new[]{SpeechWorkerState.Unloaded,SpeechWorkerState.Loading,SpeechWorkerState.Ready,SpeechWorkerState.Released,SpeechWorkerState.Degraded}){
   string[] captions={"未加载","加载中","已就绪","已释放","已降级"};string formatted=SpeechActivityText.FormatModel(new SpeechWorkerStatus(state,true,123,45,"capture_device_disconnected","native_decode_failed"));Check(formatted.Contains(captions[(int)state]),"ModelStateReflectsActualHandshake");
  }
  foreach(var state in new[]{SpeechActivityState.Calibrating,SpeechActivityState.Quiet,SpeechActivityState.Speech,SpeechActivityState.Unavailable}){
   string formatted=SpeechActivityText.FormatDiagnostic(new SpeechActivitySnapshot(state,.02,-34,-48,true,5000,false),13);Check(formatted.Contains("剩余 13 秒")&&!formatted.Contains("自动发送")&&!formatted.Contains("自动停止"),"DiagnosticNeverPromisesSendingOrRecordingStop");
  }
  long now=0;int processCalls=0,releases=0;using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>now,()=>{processCalls++;return new FakeSpeechWorker();}))using(var diagnostic=new SpeechDiagnosticSession(()=>.002,()=>now,()=>releases++,"Meter only")){
   manager.SetPreheat(true,false);diagnostic.Start();diagnostic.Sample();now=20000;diagnostic.Sample();manager.Tick();Check(!diagnostic.IsRunning&&releases==1&&processCalls==0,"TwentySecondMeterDiagnosticNeverLoadsModel");
  }
  using(var legacy=new WhisperSpeechInput(()=>new System.Diagnostics.ProcessStartInfo(),()=>0)){
   typeof(WhisperSpeechInput).GetProperty("DiagnosticCode").GetSetMethod(true).Invoke(legacy,new object[]{"capture_device_mismatch"});typeof(WhisperSpeechInput).GetProperty("LastRefinement").GetSetMethod(true).Invoke(legacy,new object[]{new SpeechRefinementResult("valid",true,false,"",null)});typeof(WhisperSpeechInput).GetProperty("DiagnosticCode").GetSetMethod(true).Invoke(legacy,new object[]{""});
   Check(((ISpeechBackendDiagnostics)legacy).Status.CaptureFailureCode=="capture_device_mismatch"&&legacy.Status.RefinementFailureCode=="","LegacySuccessfulRefinementAlsoPreservesCaptureDiagnosis");
  }
  lines.Add("PASS resident diagnostics: real model state, default-off preference, meter-only text and distinct persistent failure stages");
 }
}
}
