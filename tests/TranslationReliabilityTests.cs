using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
public static partial class Tests {
 private sealed class FailedTranslationTransport : HttpMessageHandler {
  internal int Calls;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) { Calls++; ct.ThrowIfCancellationRequested(); throw new HttpRequestException("private-token@example.com"); }
 }
 private static Exception CaptureTranslationError(Func<Task> action) { try { action().GetAwaiter().GetResult(); } catch(Exception ex){return ex;} throw new Exception("ExpectedTranslationFailure"); }
 private static void CheckTranslationKind(Exception ex,string kind) {
  var property=ex.GetType().GetProperty("Kind");
  Check(property!=null&&property.GetValue(ex,null).ToString()==kind,"StructuredTranslationError_"+kind);
 }
 internal static void RunTranslationReliabilityTests(List<string> lines) {
  foreach(var item in new[]{Tuple.Create(401,"Credentials"),Tuple.Create(403,"Credentials"),Tuple.Create(456,"Quota"),Tuple.Create(429,"RateLimit"),Tuple.Create(400,"InvalidRequest"),Tuple.Create(503,"Service")}) {
   var fake=new Fake{Status=(HttpStatusCode)item.Item1};
   var ex=CaptureTranslationError(()=>new DeepL("test:fx",fake).Translate(new[]{"hello"},"EN","ZH",CancellationToken.None));
   CheckTranslationKind(ex,item.Item2); Check(fake.Calls==(item.Item1==503?3:1),"TranslationErrorRetryPolicy_"+item.Item1);
  }
  var broken=new FailedTranslationTransport();
  CheckTranslationKind(CaptureTranslationError(()=>new DeepL("test:fx",broken).Translate(new[]{"hello"},"EN","ZH",CancellationToken.None)),"Network");
  Check(broken.Calls==3,"DeepLNetworkRetriesRemainBounded");
  var session=new RemoteAccountSession{Token="test",Granted=200000,Remaining=200000};
  var formatter=typeof(CommonMeanings).Assembly.GetType("WindowsTranslator.TranslationErrors");
  Check(formatter!=null,"TranslationErrorFormatterMustExist");
  var method=formatter.GetMethod("UserMessage",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public);
  Func<Exception,string> message=ex=>(string)method.Invoke(null,new object[]{ex});
  foreach(string code in new[]{"pool_empty","trial_empty","login_required","languages","busy","shared_unavailable","unknown"}) {
   var publicFake=new YikeApiFake{TranslateErrorCode=code}; var unused=new Fake();
   var router=new TranslationRouter("personal:fx",()=>new YikeAccountClient(publicFake),key=>new DeepL(key,unused));
   var ex=CaptureTranslationError(()=>router.TranslateProgressive(TranslationEngines.Public,session,new[]{"hello"},"EN","EN","ZH",null,null,CancellationToken.None));
   Check(ex is YikeApiException&&publicFake.TranslateCalls==1&&unused.Calls==0,"PublicFailureNeverSwitchesQuota_"+code);
   string text=message(ex); Check(!text.Contains("公共额度暂时不可用")&&!string.IsNullOrWhiteSpace(text),"PublicErrorMustUseSafeLocalMessage_"+code);
   if(code=="pool_empty"||code=="trial_empty")Check(text.Contains("额度"),"PublicQuotaMessage");
   if(code=="login_required")Check(text.Contains("登录"),"PublicSessionMessage");
   if(code=="languages")Check(text.Contains("语言"),"PublicLanguageMessage");
  }
  CheckTranslationKind(CaptureTranslationError(()=>new TranslationRouter("test:fx").TranslateProgressive(TranslationEngines.Public,session,new[]{"bonjour"},"FR","FR","DE",null,null,CancellationToken.None)),"Language");
  CheckTranslationKind(CaptureTranslationError(()=>new TranslationRouter("").TranslateProgressive(TranslationEngines.Personal,session,new[]{"hello"},"EN","EN","ZH",null,null,CancellationToken.None)),"Credentials");
  string privateText="secret-key user@example.com sensitive translation";
  Check(!message(new Exception(privateText)).Contains(privateText)&&!message(new YikeApiException(503,"unknown",privateText)).Contains(privateText),"TranslationUiNeverEchoesUnknownDetails");
  Check(message(new TimeoutException()).Contains("超时")&&message(new HttpRequestException()).Contains("网络"),"TranslationTimeoutAndNetworkMustDiffer");
  using(var cts=new CancellationTokenSource()) {
   cts.Cancel();var cancelledFake=new Fake();
   Check(CaptureTranslationError(()=>new DeepL("test:fx",cancelledFake).Translate(new[]{"hello"},"EN","ZH",cts.Token)) is OperationCanceledException,"TranslationCallerCancellationMustRemainCancellation");
  }
  lines.Add("PASS: actionable translation errors, safe messages, bounded retries and strict quota routing");
 }
}
}
