using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsTranslator {
internal sealed class CliSpeechDecoder:ISpeechFinalDecoder {
 private readonly Func<ProcessStartInfo,CancellationToken,Task<SpeechRefinement.CliOutput>> execute;
 private readonly WhisperVadConfiguration vad;
 internal CliSpeechDecoder(Func<ProcessStartInfo,CancellationToken,Task<SpeechRefinement.CliOutput>> runner,WhisperVadConfiguration configuration){execute=runner??Execute;vad=configuration;}
 public async Task<SpeechRecognitionCandidate> DecodeAsync(SpeechDecodeRequest request,CancellationToken token){
  token.ThrowIfCancellationRequested();string directory=Path.GetDirectoryName(WhisperSpeechInput.CliPath);
  string arguments=WhisperSpeechInput.RefinementArguments(request.Language,request.AudioPath);if(request.UseVad)arguments=vad.AppendArguments(arguments);
  foreach(string path in new[]{WhisperSpeechInput.ModelPath,request.AudioPath,vad.ModelPath})arguments=arguments.Replace("\""+path+"\"","\""+SpeechRefinement.NativePath(path,directory)+"\"");
  var info=new ProcessStartInfo {FileName=WhisperSpeechInput.CliPath,WorkingDirectory=directory,Arguments=arguments,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
  try{var output=await execute(info,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();return new SpeechRecognitionCandidate(output.Text,output.ExitCode,output.FailureCode,null);}
  catch(OperationCanceledException){throw;}
  catch(Exception){token.ThrowIfCancellationRequested();return new SpeechRecognitionCandidate("",-1,"cli_start_failed",null);}
 }
 private static async Task<SpeechRefinement.CliOutput> Execute(ProcessStartInfo info,CancellationToken token){
  token.ThrowIfCancellationRequested();using(Process p=Process.Start(info))using(ProcessJob job=ProcessJob.AttachOrTerminate(p))using(token.Register(()=>{job.Dispose();try{if(!p.HasExited)p.Kill();}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}})){
   Task<string> stdout=p.StandardOutput.ReadToEndAsync(),stderr=p.StandardError.ReadToEndAsync();await Task.WhenAll(stdout,stderr).ConfigureAwait(false);p.WaitForExit();token.ThrowIfCancellationRequested();
   string text=string.Join(" ",stdout.Result.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(WhisperSpeechInput.Clean).Where(s=>s.Length>0)).Trim();return new SpeechRefinement.CliOutput(p.ExitCode,text,p.ExitCode==0?"":"cli_failed");
  }
 }
}
}
