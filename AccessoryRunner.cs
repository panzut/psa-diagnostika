using System;
using System.Threading;

namespace PsaReader
{
    internal enum AccessoryProgress { Running, Completed, Rejected, Unknown }
    internal enum AccessoryOutcome { Blocked, Completed, Cancelled, TimedOut, Failed }

    // A protocol-specific implementation must supply verified commands and decode
    // their acknowledgements. There is deliberately no raw-command interface here.
    // SerialVci does NOT implement this interface until an operation is verified.
    internal interface IAccessoryOperation
    {
        string Node { get; }
        bool HasVerifiedSequence { get; }
        int MaximumDurationMs { get; }
        void Open();
        void CheckCurrentIdentityAndConditions();
        AccessoryProgress Start();
        AccessoryProgress Poll();
        bool ReturnControl();
        bool Disconnect();
    }
    internal interface IAccessoryClock
    {
        long Milliseconds { get; }
        void Wait(int milliseconds);
    }
    internal sealed class AccessoryRunResult
    {
        internal AccessoryOutcome Outcome=AccessoryOutcome.Blocked;
        internal bool StartAttempted,ReturnAttempted,ControlReturned,DisconnectAttempted,Disconnected;
        internal string Error;
        internal bool Success { get { return Outcome==AccessoryOutcome.Completed&&ControlReturned&&Disconnected; } }
        internal bool NeedsAttention { get { return (StartAttempted&&!ControlReturned)||(DisconnectAttempted&&!Disconnected); } }
    }
    internal sealed class AccessoryRunner
    {
        int running;
        internal bool IsRunning { get { return Interlocked.CompareExchange(ref running,0,0)!=0; } }
        internal AccessoryRunResult Run(IAccessoryOperation operation,IAccessoryClock clock,Func<bool> cancelled)
        {
            var result=new AccessoryRunResult();
            if(operation==null||clock==null||cancelled==null){result.Error="Chybí prováděcí profil testu.";return result;}
            // This runner is intentionally limited to BSI accessory tests. SRS,
            // unknown targets and shared-address aliases cannot inherit capability.
            if(operation.Node!="BSI"||!operation.HasVerifiedSequence||operation.MaximumDurationMs<1||operation.MaximumDurationMs>15000){
                result.Error="Pro tuto jednotku chybí ověřený postup spuštění a ukončení testu.";return result;
            }
            if(Interlocked.CompareExchange(ref running,1,0)!=0){result.Error="Jiný test už běží.";return result;}
            bool opened=false;
            try {
                if(cancelled()){result.Outcome=AccessoryOutcome.Cancelled;return result;}
                // Set before Open: even a lost acknowledgement may leave a session open.
                opened=true;operation.Open();
                operation.CheckCurrentIdentityAndConditions();
                if(cancelled()){result.Outcome=AccessoryOutcome.Cancelled;return result;}
                long start=clock.Milliseconds;
                result.StartAttempted=true;
                var progress=operation.Start();
                while(true){
                    long elapsed=clock.Milliseconds-start;
                    if(elapsed<0)throw new InvalidOperationException("Neplatný čas průběhu testu.");
                    if(cancelled()){result.Outcome=AccessoryOutcome.Cancelled;break;}
                    if(elapsed>=operation.MaximumDurationMs){result.Outcome=AccessoryOutcome.TimedOut;break;}
                    if(progress==AccessoryProgress.Completed){result.Outcome=AccessoryOutcome.Completed;break;}
                    if(progress!=AccessoryProgress.Running){result.Outcome=AccessoryOutcome.Failed;result.Error=progress==AccessoryProgress.Rejected?"Jednotka test odmítla.":"Neznámá odpověď jednotky.";break;}
                    clock.Wait((int)Math.Min(100,operation.MaximumDurationMs-elapsed));
                    // Stop/cancel/deadline is checked again before ANY subsequent poll.
                    if(cancelled()){result.Outcome=AccessoryOutcome.Cancelled;break;}
                    if(clock.Milliseconds-start>=operation.MaximumDurationMs){result.Outcome=AccessoryOutcome.TimedOut;break;}
                    progress=operation.Poll();
                }
            } catch(Exception e){result.Outcome=AccessoryOutcome.Failed;AddError(result,e.Message);}
            finally {
                try {
                    if(result.StartAttempted){
                        result.ReturnAttempted=true;
                        try{result.ControlReturned=operation.ReturnControl();if(!result.ControlReturned)AddError(result,"Návrat řízení autu nebyl potvrzen.");}
                        catch(Exception e){AddError(result,"Ukončení testu: "+e.Message);}
                    }
                    if(opened){
                        result.DisconnectAttempted=true;
                        try{result.Disconnected=operation.Disconnect();if(!result.Disconnected)AddError(result,"Ukončení spojení nebylo potvrzeno.");}
                        catch(Exception e){AddError(result,"Odpojení: "+e.Message);}
                    }
                } finally { Interlocked.Exchange(ref running,0); }
            }
            return result;
        }
        static void AddError(AccessoryRunResult result,string error){result.Error=String.IsNullOrEmpty(result.Error)?error:result.Error+"\r\n"+error;}
    }
}
