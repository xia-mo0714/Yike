using System;
using System.Net.Http;
namespace WindowsTranslator {
internal enum TranslationFailureKind { Network, Credentials, Quota, Language, RateLimit, Service, InvalidResponse, Configuration, InvalidRequest, Session }
internal sealed class TranslationFailure : InvalidOperationException {
 public TranslationFailureKind Kind { get; private set; }
 internal TranslationFailure (TranslationFailureKind kind, string message) : base (message) { Kind = kind; }
}
internal static class TranslationErrors {
 internal static string UserMessage (Exception error) {
  var known = error as TranslationFailure;
  if (known != null) return known.Message;
  if (error is TimeoutException) return "连接超时，请检查网络或代理后重试。未自动切换翻译引擎。";
  if (error is HttpRequestException) return "网络连接失败，请检查网络或代理后重试。";
  var api = error as YikeApiException;
  if (api != null) {
   switch (api.ErrorCode) {
    case "login_required": return "Yike 登录已失效，请重新登录后使用赠送额度。";
    case "email_required": return "请先完成邮箱验证，再使用赠送额度。";
    case "pool_empty": return "公共额度暂时不足，请稍后重试；如需使用自己的密钥，请在顶部手动切换引擎。";
    case "trial_empty": return "你的赠送额度不足，请缩短文字；如需使用自己的密钥，请在顶部手动切换引擎。";
    case "languages": return "赠送额度不支持当前语言组合，请调整语言或在顶部手动切换个人接入。";
    case "rate_limit": return "请求过于频繁，请稍后再试。";
    case "busy": return "Yike 翻译服务繁忙，请稍后再试。";
    case "shared_unavailable": case "usage_unavailable": return "赠送额度服务暂时不可用，请稍后重试。未改用个人密钥。";
    case "text": case "text_too_long": return "待翻译文字为空或过长，请缩短文字后重试。";
    case "request_conflict": case "request_processed": return "这次请求已处理或正在处理中，请稍候确认结果，避免重复提交。";
   }
   if (api.StatusCode == 401) return "Yike 登录已失效，请重新登录。";
   if (api.StatusCode == 429) return "Yike 服务暂时限流，请稍后再试。";
   return "Yike 翻译服务暂时不可用，请稍后再试。";
  }
  // Never echo arbitrary server bodies, credentials or source text in an error.
  return "翻译未完成，请检查输入和所选引擎的设置后重试。";
 }
}
}
