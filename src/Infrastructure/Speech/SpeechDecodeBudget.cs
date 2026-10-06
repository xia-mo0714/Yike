using System;
namespace WindowsTranslator {
internal sealed class SpeechDecodeBudget {
 private readonly object sync=new object();
 private readonly Func<long> now;
 private readonly long start;
 private readonly int limit;
 private long last;
 private int attempts;
 internal SpeechDecodeBudget(TimeSpan duration,Func<long> nowMilliseconds){
  if(nowMilliseconds==null)throw new ArgumentNullException("nowMilliseconds");
  now=nowMilliseconds;start=last=now();limit=(int)Math.Min(300000,Math.Max(30000,duration.TotalMilliseconds*4));
 }
 internal int AttemptsUsed {get{lock(sync)return attempts;}}
 internal int RemainingMilliseconds {get{lock(sync){last=Math.Max(last,now());double elapsed=(double)last-start;return(int)Math.Max(0,limit-Math.Max(0,elapsed));}}}
 internal bool TryBeginAttempt(){lock(sync){if(attempts>=2||RemainingMilliseconds<=0)return false;attempts++;return true;}}
}
}
