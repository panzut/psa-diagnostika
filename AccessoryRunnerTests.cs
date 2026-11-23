using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PsaReader
{
    internal static class AccessoryRunnerTests
    {
        sealed class Clock : IAccessoryClock
        {
            internal long Now;internal Action AfterWait;
            public long Milliseconds {get{return Now;}}
            public void Wait(int milliseconds){Now+=milliseconds;if(AfterWait!=null)AfterWait();}
        }
        sealed class Operation : IAccessoryOperation
        {
            internal readonly List<string> Calls=new List<string>();
            internal string Target="BSI",ThrowAt;
            internal bool Verified=true,ReturnOk=true,DisconnectOk=true;
            internal int Limit=300;
            internal Action OnStart,OnCheck;
            internal AccessoryProgress Initial=AccessoryProgress.Running,Next=AccessoryProgress.Completed;
            public string Node {get{return Target;}}
            public bool HasVerifiedSequence {get{return Verified;}}
            public int MaximumDurationMs {get{return Limit;}}
            void Call(string name){Calls.Add(name);if(ThrowAt==name)throw new IOException("simulated "+name);}
            public void Open(){Call("open");}
            public void CheckCurrentIdentityAndConditions(){Call("check");if(OnCheck!=null)OnCheck();}
            public AccessoryProgress Start(){Call("start");if(OnStart!=null)OnStart();return Initial;}
            public AccessoryProgress Poll(){Call("poll");return Next;}
            public bool ReturnControl(){Call("return");return ReturnOk;}
            public bool Disconnect(){Call("disconnect");return DisconnectOk;}
        }
        static int count;
        static void Check(bool ok,string label){if(!ok)throw new Exception("FAIL accessory runner: "+label);count++;}
        internal static int Run()
        {
            count=0;var runner=new AccessoryRunner();var op=new Operation();var clock=new Clock();
            var result=runner.Run(op,clock,()=>false);
            Check(result.Success&&!result.NeedsAttention,"positive result requires confirmed return and disconnect");
            Check(String.Join(",",op.Calls)=="open,check,start,poll,return,disconnect","ordered operation lifecycle");
            Check(!runner.IsRunning,"runner released after completion");
            foreach(string target in new[]{"SRS","RBG","ABS","BSM","BSR","",null}){
                op=new Operation{Target=target};result=runner.Run(op,new Clock(),()=>false);
                Check(result.Outcome==AccessoryOutcome.Blocked&&op.Calls.Count==0,"unapproved target blocked before opening: "+target);
            }
            foreach(int limit in new[]{0,-1,15001}){
                op=new Operation{Limit=limit};result=runner.Run(op,new Clock(),()=>false);
                Check(result.Outcome==AccessoryOutcome.Blocked&&op.Calls.Count==0,"invalid duration rejected before IO");
            }
            op=new Operation{Verified=false};result=runner.Run(op,new Clock(),()=>false);
            Check(result.Outcome==AccessoryOutcome.Blocked&&op.Calls.Count==0,"unverified profile never opens transport");
            op=new Operation();result=runner.Run(op,new Clock(),()=>true);
            Check(result.Outcome==AccessoryOutcome.Cancelled&&op.Calls.Count==0,"cancel before open has no IO");
            bool cancel=false;op=new Operation{OnCheck=()=>cancel=true};result=runner.Run(op,new Clock(),()=>cancel);
            Check(result.Outcome==AccessoryOutcome.Cancelled&&String.Join(",",op.Calls)=="open,check,disconnect","cancel before start cannot actuate");
            cancel=false;op=new Operation();clock=new Clock{AfterWait=()=>cancel=true};result=runner.Run(op,clock,()=>cancel);
            Check(result.Outcome==AccessoryOutcome.Cancelled&&!op.Calls.Contains("poll")&&result.ControlReturned&&result.Disconnected,"stop during wait immediately cleans up without polling");
            op=new Operation{Next=AccessoryProgress.Running};clock=new Clock();result=runner.Run(op,clock,()=>false);
            Check(result.Outcome==AccessoryOutcome.TimedOut&&clock.Now==300&&op.Calls.Count(x=>x=="poll")==2,"deadline stops before a new request");
            Check(result.ControlReturned&&result.Disconnected&&!result.Success,"timeout is not success even after cleanup");
            foreach(var progress in new[]{AccessoryProgress.Rejected,AccessoryProgress.Unknown,(AccessoryProgress)99}){
                op=new Operation{Initial=progress};result=runner.Run(op,new Clock(),()=>false);
                Check(result.Outcome==AccessoryOutcome.Failed&&op.Calls.Count(x=>x=="start")==1&&!op.Calls.Contains("poll")&&result.ControlReturned,"rejection/unknown is terminal without retry");
            }
            foreach(string phase in new[]{"open","check","start","poll","return","disconnect"}){
                op=new Operation{ThrowAt=phase};result=runner.Run(op,new Clock(),()=>false);
                Check(!result.Success&&!runner.IsRunning&&op.Calls.Count(x=>x=="disconnect")==1,"disconnect attempted and runner released after "+phase+" exception");
                Check(op.Calls.Count(x=>x=="return")==((phase=="open"||phase=="check")?0:1),"return attempted after possibly sent start: "+phase);
                if(phase=="return"||phase=="disconnect")Check(result.NeedsAttention,"cleanup uncertainty remains visible");
            }
            op=new Operation{ReturnOk=false};result=runner.Run(op,new Clock(),()=>false);
            Check(!result.Success&&result.NeedsAttention&&result.Disconnected,"negative return acknowledgement never means success");
            op=new Operation{DisconnectOk=false};result=runner.Run(op,new Clock(),()=>false);
            Check(!result.Success&&result.NeedsAttention&&result.ControlReturned,"disconnect acknowledgement distinct from return control");
            clock=new Clock();op=new Operation{Initial=AccessoryProgress.Completed,OnStart=()=>clock.Now=500};result=runner.Run(op,clock,()=>false);
            Check(result.Outcome==AccessoryOutcome.TimedOut&&!result.Success,"late successful reply cannot erase exceeded deadline");
            var second=new Operation();AccessoryRunResult nested=null;op=new Operation{OnStart=()=>nested=runner.Run(second,new Clock(),()=>false)};
            result=runner.Run(op,new Clock(),()=>false);
            Check(result.Success&&nested.Outcome==AccessoryOutcome.Blocked&&second.Calls.Count==0,"concurrent operation blocked before IO");
            Check(runner.Run(new Operation(),new Clock(),()=>false).Success,"next run possible after cleanup");
            Console.WriteLine("PASS: "+count+" accessory lifecycle checks; simulated operations only, no device IO.");return 0;
        }
    }
}
