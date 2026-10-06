using System;
namespace WindowsTranslator {
internal static class SpeechWorkerTests {
 internal static void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);}
 internal static int Main(string[] args){try{
  if(args.Length==2&&args[0]=="--native-file-contract"){SpeechNativeContractTests.RunRealDecodeContractTests(args[1]);return 0;}
  if(args.Length!=1||args[0]!="--self-test")return 2;
  SpeechNativeContractTests.RunSpeechNativeContractTests();ProtocolHostTests.Run();SpeechCaptureSessionTests.RunSpeechCaptureSessionTests();
  Console.WriteLine("ALL WORKER TESTS PASSED");return 0;
 }catch(Exception ex){Console.WriteLine("FAIL "+ex.GetType().Name+": "+ex.Message);return 1;}}
}
}
