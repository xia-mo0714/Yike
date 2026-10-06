using System;
namespace WindowsTranslator {
internal sealed class PcmWindowBuffer {
 private readonly object sync=new object();private readonly float[] ring=new float[128000];private int next,count;private bool pending;
 internal void Append(float[] samples){if(samples==null)throw new ArgumentNullException("samples");lock(sync){foreach(float sample in samples){ring[next]=sample;next=(next+1)%ring.Length;if(count<ring.Length)count++;}if(samples.Length>0)pending=true;}}
 internal float[] TakeLatestWindow(){lock(sync){if(!pending||count==0)return null;pending=false;var result=new float[count];int start=(next-count+ring.Length)%ring.Length;int first=Math.Min(count,ring.Length-start);Array.Copy(ring,start,result,0,first);if(first<count)Array.Copy(ring,0,result,first,count-first);return result;}}
}
}
