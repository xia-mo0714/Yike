using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
internal sealed class DictionaryLookup
{
 private sealed class Entry { internal DateTime Expires; internal string[] Meanings; }
 private readonly object gate = new object ();
 private readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry> (StringComparer.Ordinal);
 private readonly int capacity;
 private readonly TimeSpan lifetime;
 private readonly Func<DateTime> now;
 private readonly HashSet<CancellationTokenSource> active = new HashSet<CancellationTokenSource> ();
 private long generation;
 private bool enabled = true;
 internal DictionaryLookup (int capacity = 128, TimeSpan? lifetime = null, Func<DateTime> now = null)
 {
  if (capacity < 1) throw new ArgumentOutOfRangeException ("capacity");
  this.capacity = capacity; this.lifetime = lifetime ?? TimeSpan.FromMinutes (10);
  this.now = now ?? (() => DateTime.UtcNow);
 }
 internal void SetEnabled (bool value) {
  CancellationTokenSource[] cancelled;
  lock (gate) {
   if (enabled == value) return;
   enabled = value; generation++;
   if (value) return;
   cache.Clear (); cancelled = active.ToArray ();
  }
  foreach (var source in cancelled) try { source.Cancel (); } catch (ObjectDisposedException) { }
 }
 internal async Task<string> Enrich (HttpClient client, string query, string source, string target, string primary, CancellationToken ct)
 {
  ct.ThrowIfCancellationRequested ();
  if (!CommonMeanings.ShouldLookup (query, source, target) || string.IsNullOrWhiteSpace (primary)) return primary;
  string key = query.Trim () + "\n" + source.ToUpperInvariant () + "\n" + target.ToUpperInvariant () + "\n" + primary;
  CancellationTokenSource request;
  long ticket;
  lock (gate) {
   if (!enabled) return primary;
   Entry found;
   if (cache.TryGetValue (key, out found)) {
    if (found.Expires > now ()) return CommonMeanings.Format (primary, found.Meanings);
    cache.Remove (key);
   }
   ticket = generation;
   request = CancellationTokenSource.CreateLinkedTokenSource (ct);
   request.CancelAfter (TimeSpan.FromSeconds (2.5));
   active.Add (request);
  }
  try {
   string url = "https://dict.youdao.com/jsonapi?q=" + Uri.EscapeDataString (query.Trim ());
   using (HttpResponseMessage response = await UntilCancelled (client.GetAsync (url, HttpCompletionOption.ResponseHeadersRead, request.Token), request.Token, x => x.Dispose ())) {
    if (!response.IsSuccessStatusCode) return primary;
    const int maximumBytes = 1024 * 1024;
    if (response.Content.Headers.ContentLength > maximumBytes) return primary;
    string json;
    using (Stream stream = await UntilCancelled (response.Content.ReadAsStreamAsync (), request.Token, x => x.Dispose ()))
    using (var bytes = new MemoryStream ()) {
     byte[] buffer = new byte[8192]; int count;
     while ((count = await UntilCancelled (stream.ReadAsync (buffer, 0, buffer.Length, request.Token), request.Token)) > 0) {
      if (bytes.Length + count > maximumBytes) return primary;
      bytes.Write (buffer, 0, count);
     }
     json = Encoding.UTF8.GetString (bytes.ToArray ());
    }
    ct.ThrowIfCancellationRequested ();
    string[] meanings = CommonMeanings.Parse (json, query, source, target, primary).ToArray ();
    lock (gate) {
     if (!enabled || generation != ticket || request.IsCancellationRequested) return primary;
     if (meanings.Length > 0 && primary.Length <= 512) {
      if (cache.Count >= capacity && !cache.ContainsKey (key)) cache.Remove (cache.OrderBy (x => x.Value.Expires).First ().Key);
      cache[key] = new Entry { Expires = now ().Add (lifetime), Meanings = meanings };
     }
    }
    return CommonMeanings.Format (primary, meanings);
   }
  } catch (OperationCanceledException) { ct.ThrowIfCancellationRequested (); return primary; }
  catch { ct.ThrowIfCancellationRequested (); return primary; }
  finally { lock (gate) active.Remove (request); request.Dispose (); }
 }
 // Some handlers/content streams do not honour cancellation. Bound the caller's
 // wait too, and dispose late resources rather than publishing a stale response.
 private static async Task<T> UntilCancelled<T> (Task<T> task, CancellationToken ct, Action<T> disposeLate = null) {
  var cancelled = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
  using (ct.Register (() => cancelled.TrySetResult (true))) {
   if (await Task.WhenAny (task, cancelled.Task) != task) {
    ObserveLate (task, disposeLate);
    throw new OperationCanceledException (ct);
   }
   T result = await task;
   if (ct.IsCancellationRequested) { if (disposeLate != null) disposeLate (result); ct.ThrowIfCancellationRequested (); }
   return result;
  }
 }
 private static void ObserveLate<T> (Task<T> task, Action<T> disposeLate) {
  task.ContinueWith (late => { if (late.Status == TaskStatus.RanToCompletion) { if (disposeLate != null) disposeLate (late.Result); } else if (late.IsFaulted) { var observed = late.Exception; } }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
 }
}
}
