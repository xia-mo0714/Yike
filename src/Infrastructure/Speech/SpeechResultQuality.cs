using System;
using System.Globalization;
using System.Text.RegularExpressions;
namespace WindowsTranslator {
public enum SpeechQualityState {Unknown,Acceptable,Low}
internal static class SpeechResultQuality {
 internal sealed class Selection {
  internal string Text {get;private set;}
  internal SpeechQualityState Quality {get;private set;}
  internal bool NeedsReview {get;private set;}
  internal bool AllowAutoSubmit {get{return !NeedsReview;}}
  internal SpeechRecognitionCandidate Candidate {get;private set;}
  internal Selection(string text,SpeechQualityState quality,bool review,SpeechRecognitionCandidate candidate){Text=text;Quality=quality;NeedsReview=review;Candidate=candidate;}
 }
 internal static bool HasText(SpeechRecognitionCandidate candidate){return candidate!=null&&candidate.ExitCode==0&&!string.IsNullOrWhiteSpace(candidate.Text);}
 internal static bool TryStatistics(SpeechRecognitionCandidate candidate,out double meanLogProbability,out int count){
  meanLogProbability=double.NaN;count=0;if(!HasText(candidate))return false;
  double[] values=candidate.OrdinaryTokenProbabilities;if(values==null||values.Length==0)return false;
  double sum=0;foreach(double p in values){if(double.IsNaN(p)||double.IsInfinity(p)||p<0||p>1)return false;sum+=Math.Log(p);}
  count=values.Length;meanLogProbability=sum/count;return true;
 }
 internal static SpeechQualityState Evaluate(SpeechRecognitionCandidate candidate){
  if(!HasText(candidate))return SpeechQualityState.Unknown;
  if(HasPhraseLoop(candidate.Text))return SpeechQualityState.Low;
  double mean;int count;if(!TryStatistics(candidate,out mean,out count))return SpeechQualityState.Unknown;
  int low=0;foreach(double p in candidate.OrdinaryTokenProbabilities)if(p<0.20)low++;
  return mean< -1.0||(double)low/count>0.40?SpeechQualityState.Low:SpeechQualityState.Acceptable;
 }
 private static bool HasPhraseLoop(string text){
  try{
   var loop=new Regex(@"(?<phrase>.{12,256}?)\k<phrase>\k<phrase>",RegexOptions.Singleline|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50));
   for(Match match=loop.Match(text);match.Success;match=match.NextMatch())
    if(StringInfo.ParseCombiningCharacters(match.Groups["phrase"].Value).Length>=12)return true;
  }catch(RegexMatchTimeoutException){/* Unproven repetition is not a reason to reject text. */}
  return false;
 }
 internal static Selection Select(SpeechRecognitionCandidate first,SpeechRecognitionCandidate retry,string liveText){
  SpeechRecognitionCandidate selected=HasText(first)?first:HasText(retry)?retry:null;
  if(HasText(first)&&HasText(retry)&&Evaluate(retry)==SpeechQualityState.Acceptable){
   double firstMean,retryMean;int firstCount,retryCount;
   if(TryStatistics(first,out firstMean,out firstCount)&&TryStatistics(retry,out retryMean,out retryCount)&&
      (long)retryCount*2>=firstCount&&retryMean-firstMean>=0.15-1e-12)selected=retry;
  }
  bool hasFirst=HasText(first),hasRetry=HasText(retry);
  bool allLow=(hasFirst||hasRetry)&&(!hasFirst||Evaluate(first)==SpeechQualityState.Low)&&(!hasRetry||Evaluate(retry)==SpeechQualityState.Low);
  return new Selection(selected==null?liveText??"":selected.Text.Trim(),selected==null?SpeechQualityState.Unknown:Evaluate(selected),allLow,selected);
 }
}
}
