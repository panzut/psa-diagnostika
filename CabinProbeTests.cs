using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PsaReader
{
    internal static class CabinProbeTests
    {
        static int count;
        static void Check(bool value,string name){if(!value)throw new Exception("FAIL cabin: "+name);count++;}
        static void Reject(Action action,string name){bool rejected=false;try{action();}catch(IOException){rejected=true;}catch(InvalidOperationException){rejected=true;}Check(rejected,name);}
        sealed class Fake : ICabinIdentityVci
        {
            internal string Bsi="*255",Bsm="*255";
            internal bool Timeout,BadIdle,BadCleanup,BadConfigure,BadFirmware;
            internal int Queries,Disconnects,Configurations;
            internal string SessionReply="*80 192",ExitReply="*80 129";
            internal int Entries,Exits;
            public string CabinSession(CanRoute route,bool enter){CanRoute.Validate(route);if(enter){Entries++;return SessionReply;}Exits++;return ExitReply;}
            public string Exchange(VciCommand command){
                switch(command){
                    case VciCommand.Firmware:return BadFirmware?"*invalid":"*1423";
                    case VciCommand.Status:return BadIdle?"*1 1450":"*0 1450";
                    case VciCommand.Model:return "*CDP+";
                    case VciCommand.Serial:return "*123456";
                    case VciCommand.Pins:return "*114";
                    case VciCommand.Relays:return "*106";
                    case VciCommand.Timing:return "*0 10";
                    case VciCommand.ReceiveMode:return "*256";
                    case VciCommand.Disconnect:Disconnects++;return BadCleanup?"*0":"*1";
                    default:throw new Exception("Cabin identification sent forbidden command: "+command);
                }
            }
            public string ConfigureCabin(CabinWiring wiring){Configurations++;return BadConfigure?"*255":"*256";}
            public string ReadCabin(CabinRecord record){Queries++;if(Timeout)throw new TimeoutException("simulated timeout");return record==CabinRecord.Bsi80?Bsi:Bsm;}
            public void Dispose(){}
        }
        internal static int Run()
        {
            // 61 8B, 12 data bytes: hardware BCD, supplier 0013, additional BCD.
            const string bsm="*97 139 150 102 112 3 128 0 19 150 102 112 5 128";
            // 61 80, 17 data bytes, reference 9664058780.
            const string bsi="*97 128 150 100 5 135 128 0 0 0 0 0 0 0 0 0 0 0 0";
            var r=CabinProbe.Decode(CabinRecord.Bsm8B,bsm);
            Check(r.Responded&&r.Reference=="9666700580"&&r.HardwareReference=="9666700380","BSM references use distinct offsets and decimal adapter bytes");
            r=CabinProbe.Decode(CabinRecord.Bsi80,bsi);
            Check(r.Responded&&r.Reference=="9664058780"&&r.HardwareReference==null,"BSI reference separate from BSM");
            Check(!CabinProbe.Decode(CabinRecord.Bsi80,"*255").Responded,"adapter FF is not ECU presence");
            r=CabinProbe.Decode(CabinRecord.Bsm8B,"*127 33 18");
            Check(r.Responded&&r.Reference==null,"negative ECU reply proves path but not identity");
            r=CabinProbe.Decode(CabinRecord.Bsm8B,"*97 139 1 2 3");
            Check(r.Responded&&r.Reference==null,"unrecognized layout retained without invented reference");
            Reject(()=>CabinProbe.Decode(CabinRecord.Bsi80,bsm),"wrong record cannot identify BSI");
            Reject(()=>CabinProbe.Decode(CabinRecord.Bsi80,"*127 16 18"),"unrelated negative response rejected");
            Reject(()=>CabinProbe.Decode(CabinRecord.Bsm8B,"*256"),"adapter status not ECU bytes");
            Reject(()=>CabinProbe.Decode(CabinRecord.Bsm8B,bsm.Replace("150 102 112 5 128","250 102 112 5 128")),"malformed BCD rejected");
            Reject(()=>CabinProbe.ConfigureCommand((CabinWiring)50),"no arbitrary wiring");
            Reject(()=>CabinProbe.ReadCommand((CabinRecord)50),"no arbitrary services");
            var fake=new Fake();var routes=CabinProbe.RunRoutes(fake,delegate{});
            Check(routes.Count==2&&fake.Queries==4&&fake.Disconnects==2&&routes.All(x=>x.Disconnected),"bounded two-route test, both records, cleanup");
            fake=new Fake{Bsi=bsi,Bsm=bsm};routes=CabinProbe.RunRoutes(fake,delegate{});
            Check(routes.Count==1&&fake.Queries==2&&fake.Disconnects==1,"responding route stops wiring search, records stay separate");
            fake=new Fake{Bsi="*127 33 18"};routes=CabinProbe.RunRoutes(fake,delegate{});
            Check(routes.Count==1,"negative reply also stops wiring search");
            fake=new Fake{Timeout=true};routes=CabinProbe.RunRoutes(fake,delegate{});
            Check(routes.Count==1&&fake.Queries==1&&fake.Disconnects==1&&routes[0].Disconnected,"timeout stops all further queries and still disconnects");
            fake=new Fake{BadCleanup=true};routes=CabinProbe.RunRoutes(fake,delegate{});
            Check(routes.Count==1&&!routes[0].Disconnected&&fake.Configurations==1,"failed cleanup prevents switching wiring");
            fake=new Fake{BadConfigure=true};routes=CabinProbe.RunRoutes(fake,delegate{});
            Check(routes.Count==1&&fake.Queries==0&&fake.Disconnects==1,"failed setup prevents ECU requests and recovers adapter");
            fake=new Fake{BadIdle=true};Reject(()=>CabinProbe.RunRoutes(fake,delegate{}),"active adapter blocked before configuration");
            Check(fake.Configurations==0&&fake.Queries==0,"active adapter sends no vehicle commands");
            fake=new Fake{BadFirmware=true};Reject(()=>CabinProbe.RunRoutes(fake,delegate{}),"malformed firmware identity blocked");
            Check(fake.Configurations==0,"firmware check never updates anything");
            fake=new Fake{Bsi=bsi,Bsm=bsm};routes=CabinProbe.RunRoutes(fake,delegate{},true);
            Check(routes.Count==1&&fake.Entries==1&&fake.Exits==1&&fake.Queries==2&&routes[0].Disconnected,"captured diagnostic session returns to default before adapter disconnect");
            fake=new Fake{SessionReply="*127 16 34"};routes=CabinProbe.RunRoutes(fake,delegate{},true);
            Check(fake.Queries==0&&fake.Exits==0&&fake.Disconnects==1&&routes[0].Disconnected,"rejected session sends no identity queries");
            fake=new Fake{SessionReply="*255"};routes=CabinProbe.RunRoutes(fake,delegate{},true);
            Check(fake.Queries==0&&fake.Exits==0&&fake.Disconnects==1,"no-response session not retried");
            fake=new Fake{ExitReply="*255"};routes=CabinProbe.RunRoutes(fake,delegate{},true);
            Check(!routes[0].Disconnected&&fake.Disconnects==1,"session exit failure remains visible even after bridge disconnect");
            fake=new Fake{SessionReply="*80 3"};routes=CabinProbe.RunRoutes(fake,delegate{},true);
            Check(fake.Queries==0&&fake.Exits==1&&fake.Disconnects==1,"unknown session reply triggers default-session recovery");
            Console.WriteLine("PASS: "+count+" cabin identification checks; simulated adapter only.");return 0;
        }
    }
}

