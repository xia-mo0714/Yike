using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
namespace WindowsTranslator {
// Modules belong to the worker's lifetime. Registered ggml backends may keep thread
// pools after whisper_free, so OS process exit, not FreeLibrary, releases them.
internal sealed class NativeLibraryLoader {
 internal static readonly Dictionary<string,string> Hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){
  {"ggml-base.dll","a5241b52206f61c9dfd6f0f53a8ef19076f9528ead2db1e039a66d7e03817bfc"},{"ggml.dll","c6e88687d6aa0238f2e834a87958f38d2dc96075a6a7bf6fb8b9cd728a0faed2"},
  {"whisper.dll","b7bb4ba92bd36b8afe0c00059ca6a0f69c6767193bfb5f0761e9ed27b1087803"},{"SDL2.dll","de23db1694a3c7a4a735e7ecd3d214b2023cc2267922c6c35d30c7fc7370d677"},
  {"ggml-cpu-alderlake.dll","5a5b11dcd38e321b13f85c95414940db9eab1132be3da6342f03dfb1d8e51bd5"},{"ggml-cpu-cannonlake.dll","2c858781450b52eda95c381232cc65c9e19cbf621cc7b254d8c44fdbab77791e"},
  {"ggml-cpu-cascadelake.dll","ddf49bb749b34800afcb3d6224544966a05c5d00f1d0b6565bee9f3b010dd53c"},{"ggml-cpu-haswell.dll","6b772e094b8976e22b4c043be86a1a8deda4b50511dc803a693b652c51b24944"},
  {"ggml-cpu-icelake.dll","bbd87ea5920edc401054848071ad2ef8c96bbf8007c4e4cd0ea82ba9b9e3bd10"},{"ggml-cpu-sandybridge.dll","de2ad5f84c7dfa557d515b3678d6452ce0216590268b2ca79cc537fe87cf239d"},
  {"ggml-cpu-skylakex.dll","9fa3f9d984ca42d568181dd345072299db2978b328800343218c2c7f000b7152"},{"ggml-cpu-sse42.dll","740fc769ef433985dfdb24a609a42d5acf188abda52287a4d89b89b567507c19"},
  {"ggml-cpu-x64.dll","43ccf32b9b70aa4c47d0a12ac2ed3c241e0045571acdad565ecd9d1f99ffc08b"}};
 private readonly Dictionary<string,IntPtr> modules=new Dictionary<string,IntPtr>(StringComparer.OrdinalIgnoreCase);
 private IntPtr directoryCookie;
 private const uint SearchDllLoadDirectory=0x100,SearchUserDirectories=0x400,SearchSystem32=0x800;
 [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetDefaultDllDirectories(uint flags);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr AddDllDirectory(string directory);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
 [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)]private static extern IntPtr GetProcAddress(IntPtr module,string name);
 [DllImport("kernel32.dll")]private static extern uint GetACP();
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int ScoreCall();
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate IntPtr LoadBackendCall(IntPtr path);
 internal static bool HasExpectedHash(string path,string sha,long size){try{
  AssertPlainPath(path);var info=new FileInfo(path);if(!info.Exists||(size>=0&&info.Length!=size))return false;
  using(var hash=SHA256.Create())using(var file=File.OpenRead(path))return string.Equals(BitConverter.ToString(hash.ComputeHash(file)).Replace("-",""),sha,StringComparison.OrdinalIgnoreCase);
 }catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}}
 internal static void AssertPlainPath(string path){
  string current=Path.GetFullPath(path);while(!string.IsNullOrEmpty(current)){
   if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("native_reparse_path");
   string parent=Path.GetDirectoryName(current);if(string.Equals(parent,current,StringComparison.OrdinalIgnoreCase))break;current=parent;
  }
 }
 internal static NativeLibraryLoader Open(SpeechRuntimePaths paths){
  if(paths==null)throw new ArgumentNullException("paths");
  if(!WhisperNative.ValidateLayout(Path.Combine(paths.ApplicationRoot,"whisper-1.9.4-abi.json")))throw new InvalidDataException("native_abi_mismatch");
  foreach(var item in Hashes)if(!HasExpectedHash(Path.Combine(paths.RuntimeRoot,item.Key),item.Value,-1))throw new InvalidDataException("native_dependency_invalid");
  foreach(string file in Directory.GetFiles(paths.RuntimeRoot,"*.dll"))if(!Hashes.ContainsKey(Path.GetFileName(file)))throw new InvalidDataException("native_unknown_dependency");
  if(GetACP()!=65001)throw new InvalidDataException("native_utf8_code_page_required");
  var loader=new NativeLibraryLoader();if(!SetDefaultDllDirectories(SearchUserDirectories|SearchSystem32))throw new InvalidDataException("native_safe_search_failed");
  loader.directoryCookie=AddDllDirectory(paths.RuntimeRoot);if(loader.directoryCookie==IntPtr.Zero)throw new InvalidDataException("native_safe_search_failed");
  foreach(string name in new[]{"ggml-base.dll","ggml.dll","whisper.dll"})loader.Load(paths.RuntimeRoot,name);
  int bestScore=0;string best=null;
  foreach(var item in Hashes)if(item.Key.StartsWith("ggml-cpu-",StringComparison.Ordinal)){
   loader.Load(paths.RuntimeRoot,item.Key);int score=loader.Export<ScoreCall>(item.Key,"ggml_backend_score")();if(score>bestScore){bestScore=score;best=item.Key;}
  }
  if(best==null)throw new InvalidDataException("native_cpu_unavailable");
  using(var path=new WhisperNative.Utf8(Path.Combine(paths.RuntimeRoot,best)))if(loader.Export<LoadBackendCall>("ggml.dll","ggml_backend_load")(path.Pointer)==IntPtr.Zero)throw new InvalidDataException("native_cpu_initialization_failed");
  return loader;
 }
 internal void Load(string root,string name){if(modules.ContainsKey(name))return;var module=LoadLibraryEx(Path.Combine(root,name),IntPtr.Zero,SearchDllLoadDirectory|SearchSystem32);if(module==IntPtr.Zero)throw new InvalidDataException("native_load_failed");modules.Add(name,module);}
 internal T Export<T>(string module,string name)where T:class{
  IntPtr handle,address;if(!modules.TryGetValue(module,out handle)||(address=GetProcAddress(handle,name))==IntPtr.Zero)throw new InvalidDataException("native_missing_export");
  return (T)(object)Marshal.GetDelegateForFunctionPointer(address,typeof(T));
 }
}
}
