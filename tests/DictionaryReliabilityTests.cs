using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
public static partial class Tests {
 private sealed class DictionaryResponse : HttpMessageHandler {
  internal int Calls;
  internal HttpStatusCode Status = HttpStatusCode.OK;
  internal string Body = "{\"ec\":{\"word\":[{\"trs\":[{\"tr\":[{\"l\":{\"i\":[\"n. 银行；河岸\"]}}]}]}]}}";
  internal Func<HttpContent> Content;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
   Calls++;ct.ThrowIfCancellationRequested();
   Check(request.RequestUri.Host=="dict.youdao.com"&&!request.Headers.Contains("Authorization"),"DictionaryRequestMustNotSendCredentials");
   return Task.FromResult(new HttpResponseMessage(Status){Content=Content!=null?Content():new StringContent(Body)});
  }
 }
 private sealed class DictionaryStreamContent : HttpContent {
  internal Stream Stream;
  protected override bool TryComputeLength(out long length) { length=0; return false; }
  protected override Task SerializeToStreamAsync(Stream stream,TransportContext context) { throw new NotSupportedException(); }
  protected override Task<Stream> CreateContentReadStreamAsync() { return Task.FromResult(Stream); }
 }
 private sealed class HeldDictionaryStream : MemoryStream {
  internal bool Released;
  private readonly TaskCompletionSource<int> pending=new TaskCompletionSource<int>();
  public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken ct) { return pending.Task; }
  protected override void Dispose(bool disposing) { Released=true; base.Dispose(disposing); }
 }
 private sealed class HeldDictionaryResponse : HttpMessageHandler {
  internal CancellationToken Token;
  internal readonly TaskCompletionSource<HttpResponseMessage> Response = new TaskCompletionSource<HttpResponseMessage>();
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) { Token=ct; return Response.Task; }
  internal void Release() { Response.SetResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new DictionaryResponse().Body)}); }
 }
 internal static void RunDictionaryReliabilityTests(List<string> lines){
  string longPrimary="这是主翻译引擎返回的完整译文，包含需要保留的补充说明，不能因为长度超过词典释义限制而被丢弃。";
  string longResult=CommonMeanings.Format(longPrimary,new[]{"银行","河岸"});
  Check(longResult.Contains("1. "+longPrimary),"DictionaryMustPreserveCompletePrimaryTranslation");
  var field=typeof(CommonMeanings).GetField("client",BindingFlags.NonPublic|BindingFlags.Static);
  object original=field.GetValue(null);var transport=new DictionaryResponse();
  using(var client=new HttpClient(transport))try {
   field.SetValue(null,client);
   string first=CommonMeanings.Enrich("bank","EN-US","ZH-HANS","银行",CancellationToken.None).GetAwaiter().GetResult();
   string again=CommonMeanings.Enrich("bank","EN-US","ZH-HANS","银行",CancellationToken.None).GetAwaiter().GetResult();
   Check(first=="常用释义"+Environment.NewLine+"1. 银行"+Environment.NewLine+"2. 河岸"&&again==first,"DictionaryCacheMustPreservePrimaryAndMeanings");
   Check(transport.Calls==1,"DictionaryRepeatedLookupMustUseCache");
  } finally {field.SetValue(null,original); CommonMeanings.SetEnabled(false); CommonMeanings.SetEnabled(true);}
  lines.Add("PASS: repeated dictionary lookup uses cache without losing primary translation");
  DateTime time=new DateTime(2026,10,6,0,0,0,DateTimeKind.Utc);
  var lookup=new DictionaryLookup(2,TimeSpan.FromMinutes(10),()=>time);
  var bounded=new DictionaryResponse();
  using(var client=new HttpClient(bounded)) {
   Func<string,string> enrich=q=>lookup.Enrich(client,q,"EN","ZH","银行",CancellationToken.None).GetAwaiter().GetResult();
   enrich("bank"); time=time.AddSeconds(1); enrich("shore"); time=time.AddSeconds(1); enrich("river"); enrich("bank");
   Check(bounded.Calls==4,"DictionaryCacheMustEvictAtCapacity");
   time=time.AddMinutes(11); enrich("bank"); Check(bounded.Calls==5,"DictionaryCacheMustExpire");
   lookup.Enrich(client,"bank","EN","ZH","河岸",CancellationToken.None).GetAwaiter().GetResult();
   Check(bounded.Calls==6,"DictionarySenseMustIncludePrimary");
   lookup.SetEnabled(false); Check(enrich("bank")=="银行"&&bounded.Calls==6,"DisabledDictionaryMustNotRequest");
   lookup.SetEnabled(true); enrich("bank"); Check(bounded.Calls==7,"DisablingMustClearDictionaryCache");
   bounded.Status=HttpStatusCode.ServiceUnavailable;
   Check(enrich("failure")=="银行"&&enrich("failure")=="银行"&&bounded.Calls==9,"DictionaryFailureMustNotCacheOrLosePrimary");
   bounded.Status=HttpStatusCode.OK; bounded.Body="not json";
   Check(enrich("malformed")=="银行","MalformedDictionaryMustKeepPrimary");
   bounded.Body=new string(' ',1024*1024)+new DictionaryResponse().Body;
   Check(enrich("oversize")=="银行","OversizedDictionaryMustKeepPrimary");
   bounded.Content=()=>new DictionaryStreamContent{Stream=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bounded.Body))};
   Check(enrich("chunked")=="银行","ChunkedDictionaryMustHaveByteLimit");
   using(var cancelled=new CancellationTokenSource()) {
    cancelled.Cancel(); bool rejected=false;
    try {lookup.Enrich(client,"bank","EN","ZH","银行",cancelled.Token).GetAwaiter().GetResult();} catch(OperationCanceledException){rejected=true;}
    Check(rejected,"DictionaryCancelledCallerMustNotReturnCache");
   }
  }
  var bodyStream=new HeldDictionaryStream(); var bodyTransport=new DictionaryResponse{Content=()=>new DictionaryStreamContent{Stream=bodyStream}};
  using(var client=new HttpClient(bodyTransport)) {
   Task<string> pending=new DictionaryLookup().Enrich(client,"bank","EN","ZH","银行",CancellationToken.None);
   Check(Task.WhenAny(pending,Task.Delay(4000)).GetAwaiter().GetResult()==pending&&pending.GetAwaiter().GetResult()=="银行"&&bodyStream.Released,"DictionaryBodyDeadlineMustDisposeStream");
  }
  var callerHeld=new HeldDictionaryResponse();
  using(var client=new HttpClient(callerHeld)) using(var cts=new CancellationTokenSource()) {
   Task<string> pending=new DictionaryLookup().Enrich(client,"bank","EN","ZH","银行",cts.Token); cts.Cancel(); callerHeld.Release();
   bool cancelled=false;try{pending.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
   Check(cancelled,"CallerCancellationMustSuppressLateDictionaryResult");
  }
  var held=new HeldDictionaryResponse(); var policy=new DictionaryLookup();
  using(var client=new HttpClient(held)) {
   Task<string> pending=policy.Enrich(client,"bank","EN","ZH","银行",CancellationToken.None);
   policy.SetEnabled(false); Check(held.Token.IsCancellationRequested,"DisablingMustCancelInFlightDictionary");
   policy.SetEnabled(true); held.Release();
   Check(pending.GetAwaiter().GetResult()=="银行","OldDictionaryGenerationMustNotPublishAfterReenable");
  }
  var timed=new HeldDictionaryResponse();
  using(var client=new HttpClient(timed)) {
   Task<string> pending=new DictionaryLookup().Enrich(client,"bank","EN","ZH","银行",CancellationToken.None);
   bool finished=Task.WhenAny(pending,Task.Delay(4000)).GetAwaiter().GetResult()==pending;
   timed.Release();
   Check(finished&&pending.GetAwaiter().GetResult()=="银行","DictionaryDeadlineMustBoundTransportAndKeepPrimary");
  }
  var preference=typeof(Preferences).GetField("OnlineDictionary");
  Check(preference!=null,"DictionaryPreferenceMustExist");
  Check((bool)preference.GetValue(Store.Json.Deserialize<Preferences>("{}")),"ExistingPreferencesKeepDictionaryDefault");
  var disabled=new Preferences(); preference.SetValue(disabled,false);
  Check(!(bool)preference.GetValue(Store.Json.Deserialize<Preferences>(Store.Json.Serialize(disabled))),"DictionaryPreferenceMustRoundTrip");
  lines.Add("PASS: dictionary privacy gate, bounded cache, expiration, failures and cancellation");
 }
}
}
