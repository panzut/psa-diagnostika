using System;
using System.IO;
using System.Linq;
using System.Text;

namespace PsaReader
{
    // A short, read-only inspection of the actual ECU, including the traceability
    // record omitted by the imported catalogue. No unlock or write service.
    internal static class BsiCodingReadiness
    {
        internal static int Run()
        {
            var catalogue=Catalogue.Load();var vehicle=catalogue.Vehicles.Single(v=>v.Id=="VCAN:all");
            var route=CanRoute.Plan(catalogue,vehicle).Single(r=>r.Id=="752:652"&&r.UseCabinPins);
            string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs");Directory.CreateDirectory(folder);
            string path=Path.Combine(folder,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-bsi-coding-readiness.txt");
            bool success=false;
            using(var log=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read),Encoding.UTF8)){
                log.AutoFlush=true;Action<string> record=s=>{log.WriteLine(DateTimeOffset.Now.ToString("o")+" "+s);Console.WriteLine(s);};
                record("READ ONLY: BSI identification, secured traceability, lighting configuration and power status. No unlocking or writes.");
                try{using(var wire=new SerialVci(record)){
                    bool touched=false,session=false;
                    try{
                        Discovery.CheckAdapter(wire);touched=true;
                        VciProtocol.Expect(wire.Exchange(VciCommand.Pins),"*114");VciProtocol.Expect(wire.Exchange(VciCommand.Relays),"*106");
                        VciProtocol.Expect(wire.Configure(route),"*256");VciProtocol.Expect(wire.Exchange(VciCommand.Timing),"*0 10");VciProtocol.Expect(wire.Exchange(VciCommand.ReceiveMode),"*256");
                        session=true;var reply=Discovery.EcuBytes(wire.CabinSession(route,true));
                        if(reply.Length!=2||reply[0]!=0x50||reply[1]!=0xC0)throw new IOException("BSI nepotvrdila diagnostickou relaci.");
                        foreach(int id in new[]{0x80,0xA0,0xB9,0xC0}){
                            var data=BulkReader.Classify(0x21,id,"Příprava zápisu",Discovery.EcuBytes(wire.ReadRecord(new ReadRecord{Service=0x21,Identifier=id})));
                            record("READ 21 "+id.ToString("X2")+" "+data.State+" / "+data.Raw);
                            if(data.State!="Přečteno")throw new IOException("Potřebný záznam "+id.ToString("X2")+" nebyl přečten.");
                            if(id==0x80&&data.Raw!="61 80 96 64 05 87 80 00 2C 96 64 05 87 80 05 01 FF FF 0A 40 52 11 47 99")throw new IOException("Identifikace se liší od doložené BSI. Další kontrola zastavena.");
                        }
                        record("Inspection complete. Readable records do NOT establish write support. No write profile has been enabled.");
                        success=true;
                    }finally{if(touched){try{Discovery.Close(wire,route,session);record("DISCONNECTED confirmed");}catch{success=false;throw;}}}
                }}catch(Exception e){record("FAILED "+e.Message);success=false;}
            }
            Console.WriteLine("Záznam: "+path);return success?0:1;
        }
    }
}
