using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PsaReader
{
    internal enum BsiAction { DaylightOff, DaylightOn, EconomyOff, ServiceReset }
    internal interface IBsiServiceVci : IBulkVci { string ExecuteBsi(BsiAction action,bool engineOff); string SetServiceInterval(int km,bool engineOff); string EconomyResult(); void UnlockBsiConfiguration(bool engineOff); }
    internal static class BsiServiceProfile
    {
        internal const string Identity="61 80 96 64 05 87 80 00 2C 96 64 05 87 80 05 01 FF FF 0A 40 52 11 47 99";
        internal const string Software="61 FE 40 AD 00 00 01 B0 00 00 00 01 08 04 0C 10 04 09 FE 02 00 00 01 01 45 88";
        internal const string EconomyStart="*608_31_AD_00_00_C8_01",EconomyPoll="*608_31_AD_01";
        internal static readonly int[] LightingMap={81,22,108,109,114,138,117,118,25,45,0,77,98,132,19,20,60,122,123,140,141,165,51,112,43,49,104,167,103,50,35,37,21,172,28,136,6,48,96,86,124,121,148,97,24,16,57,59,67,102,63,159};
        internal static readonly int[] PowerMap={44,71,63,64,21,22,37,40,41,42,51,53,54,55,56,66,67,69,73,74,1,5,12,15,33,35,47,49,50,52,59,60,62,65,68,72};
        internal static string Hex(int[] b){return String.Join(" ",b.Select(x=>x.ToString("X2")));}
        internal static void Record(int[] b,int lid,int length)
        {if(b==null||b.Length!=length+2||b[0]!=0x61||b[1]!=lid||b.Any(x=>x<0||x>255))throw new IOException("Neúplná nebo odmítnutá odpověď BSI "+lid.ToString("X2")+".");}
        internal static void Power(int[] b,bool changing)
        {
            Record(b,0xC0,30);
            if((b[30]&8)==0)throw new IOException("BSI nepotvrdila zapnuté zapalování.");
            if(b[14]<=121||b[14]>155)throw new IOException("Napětí baterie není v rozmezí nad 12,1 V až 15,5 V: "+(b[14]/10.0).ToString("0.0",CultureInfo.InvariantCulture)+" V.");
            // Heated-seat authorisation is NOT an engine-speed measurement and may
            // remain asserted after stopping. Engine-off is explicitly confirmed
            // by the operator; never infer it from this accessory-output bit.
        }
        internal static int[] Daylight(int[] original,bool enabled)
        {
            Record(original,0xB9,22);
            if(original[18]!=1&&original[18]!=2)throw new IOException("Typ denního svícení neodpovídá zaznamenanému profilu tohoto auta.");
            var b=(int[])original.Clone();
            // Mapped B9 index 20, payload byte 17: value 01 = no daytime running lamps.
            // Value 02 restores this car's original captured configuration (not a
            // universal definition of value 02 for other vehicles).
            b[18]=enabled?2:1;b[19]=enabled?b[19]|1:b[19]&~1;return b;
        }
        internal static string DaylightCommand(int[] original,bool enabled)
        {
            // PSA BSI full-record envelope: 3B <LID> FF <record bytes>.
            // Initially derived from 807 CAN BSI full-record procedures; actual B9
            // acknowledgements and post-reconnect readback verified 2026-11-22.
            // Never send a raw 21 response including its 61 B9 header.
            return "*608_3B_B9_FF_"+String.Join("_",Daylight(original,enabled).Skip(2).Select(x=>x.ToString("X2")));
        }
        internal static void CheckMap(IEnumerable<DataRead> pages,int root,int lid,int[] expected)
        {var table=MappedDecoder.Table(pages,root);if(!table.ContainsKey(lid)||!table[lid].SequenceEqual(expected))throw new IOException("Rozložení BSI se liší od ověřeného profilu; operace zastavena.");}
    }
    internal sealed class BsiServiceResult
    {
        internal bool Success,Changed,Disconnected;
        internal string Report,LogPath;
    }
    internal static class BsiServices
    {
        internal static CanRoute Route(Catalogue c,VehicleDefinition v)
        {if(v.Id!="VCAN:all"||c.Revision!=Accessories.Revision)throw new IOException("Servisní profil není určený pro toto vozidlo/databázi.");return CanRoute.Plan(c,v).Single(r=>r.Id=="752:652"&&r.UseCabinPins);}
        internal static int[] Read(IBsiServiceVci wire,int lid)
        {var b=Discovery.EcuBytes(wire.ReadRecord(new ReadRecord{Service=0x21,Identifier=lid}));if(b.Length<2||b[0]!=0x61||b[1]!=lid)throw new IOException("BSI odmítla čtení "+lid.ToString("X2")+": "+BsiServiceProfile.Hex(b));return b;}
        internal static void Open(IBsiServiceVci wire,CanRoute route)
        {
            Discovery.CheckAdapter(wire);
            VciProtocol.Expect(wire.Exchange(VciCommand.Pins),"*114");VciProtocol.Expect(wire.Exchange(VciCommand.Relays),"*106");
            VciProtocol.Expect(wire.Configure(route),"*256");VciProtocol.Expect(wire.Exchange(VciCommand.Timing),"*0 10");VciProtocol.Expect(wire.Exchange(VciCommand.ReceiveMode),"*256");
            if(!Discovery.EcuBytes(wire.CabinSession(route,true)).SequenceEqual(new[]{0x50,0xC0}))throw new IOException("BSI nepotvrdila diagnostickou relaci.");
            if(BsiServiceProfile.Hex(Read(wire,0x80))!=BsiServiceProfile.Identity||BsiServiceProfile.Hex(Read(wire,0xFE))!=BsiServiceProfile.Software)throw new IOException("Připojená BSI má jinou identifikaci nebo software.");
        }
        internal static void Map(IBsiServiceVci wire,CanRoute route,int root)
        {
            var pages=new List<DataRead>();
            for(int page=1;page<=32;page++){
                var b=Discovery.EcuBytes(wire.ReadMap(route,root,page));
                if(b.Length<9||b[0]!=0x75||b[1]!=root)throw new IOException("BSI odmítla tabulku parametrů.");
                pages.Add(new DataRead{Service=0x35,Identifier=root,Page=page,State="Přečteno",Raw=BsiServiceProfile.Hex(b)});
                if(b[2]==0xFF){BsiServiceProfile.CheckMap(pages,root,root==0xB0?0xB9:0xC0,root==0xB0?BsiServiceProfile.LightingMap:BsiServiceProfile.PowerMap);return;}
            }throw new IOException("Nedokončená tabulka parametrů.");
        }
        internal static BsiServiceResult Session(IBsiServiceVci wire,CanRoute route,BsiAction action,bool engineOff,Action<string> log,int intervalKm=10000)
        {
            if(action==BsiAction.ServiceReset)return ServiceMaintenance.Session(wire,route,intervalKm,engineOff,log);
            var result=new BsiServiceResult();bool opened=false,writeAttempted=false;string outcome=null;
            try{
                if(!engineOff)throw new IOException("Nejdříve vypněte motor a ponechte zapnuté zapalování.");
                opened=true;Open(wire,route);log("Ověřuji aktuální rozložení parametrů BSI…");
                Map(wire,route,0xC0);if(action!=BsiAction.EconomyOff)Map(wire,route,0xB0);
                int[] original=action==BsiAction.EconomyOff?null:Read(wire,0xB9);
                int[] power=Read(wire,0xC0);BsiServiceProfile.Power(power,true);
                log("Napětí BSI: "+(power[14]/10.0).ToString("0.0",CultureInfo.InvariantCulture)+" V. Identifikace a tabulky souhlasí.");
                if(action==BsiAction.EconomyOff){
                    if((power[30]&4)==0){outcome="Úsporný režim už je neaktivní. Žádný změnový povel nebyl odeslán.";}
                    else{
                        log("Spouštím doloženou rutinu vypnutí úsporného režimu…");writeAttempted=true;
                        var ack=Discovery.EcuBytes(wire.ExecuteBsi(action,engineOff));RequireRoutine(ack);
                        var status=Discovery.EcuBytes(wire.EconomyResult());RequireRoutine(status);
                        log("Odpověď rutiny: "+BsiServiceProfile.Hex(status)+". Rozhoduje kontrolní odečet stavu.");
                        var after=Read(wire,0xC0);BsiServiceProfile.Power(after,false);
                        if((after[30]&4)!=0)throw new IOException("BSI po rutině stále hlásí aktivní úsporný režim.");
                        result.Changed=true;outcome="Kontrolní odečet potvrdil neaktivní úsporný režim. Potlačení je dočasné.";
                    }
                }else{
                    bool enabled=action==BsiAction.DaylightOn;var expected=BsiServiceProfile.Daylight(original,enabled);
                    if(original.SequenceEqual(expected))outcome="Denní svícení v konfiguraci už je "+(enabled?"zapnuté":"vypnuté")+". Zápis nebyl potřeba.";
                    else{
                        // Persisted by the auto-flushed log BEFORE the only write attempt.
                        log("BACKUP B9 "+BsiServiceProfile.Hex(original));log("EXPECTED B9 "+BsiServiceProfile.Hex(expected));
                        // Leave the map-upload dialogue before requesting coding access.
                        // Only the documented diagnostic-session transition is used.
                        if(!Discovery.EcuBytes(wire.CabinSession(route,false)).SequenceEqual(new[]{0x50,0x81}))throw new IOException("BSI nepotvrdila konec čtecí relace.");
                        if(!Discovery.EcuBytes(wire.CabinSession(route,true)).SequenceEqual(new[]{0x50,0xC0}))throw new IOException("BSI nepotvrdila novou relaci.");
                        if(BsiServiceProfile.Hex(Read(wire,0x80))!=BsiServiceProfile.Identity||BsiServiceProfile.Hex(Read(wire,0xFE))!=BsiServiceProfile.Software)throw new IOException("Identifikace nové relace nesouhlasí.");
                        if(!Read(wire,0xB9).SequenceEqual(original))throw new IOException("Nastavení se během přípravy změnilo.");
                        BsiServiceProfile.Power(Read(wire,0xC0),true);
                        log("Povoluji konfiguraci identifikované JCAE BSI, jedním výpočtem odpovědi na její výzvu…");
                        wire.UnlockBsiConfiguration(engineOff);
                        // Re-read after security access; do not use a configuration that
                        // changed while unlocking, or power data older than the challenge.
                        if(!Read(wire,0xB9).SequenceEqual(original))throw new IOException("Nastavení se během přípravy změnilo. Žádný zápis nebude proveden.");
                        BsiServiceProfile.Power(Read(wire,0xC0),true);
                        writeAttempted=true;var ack=Discovery.EcuBytes(wire.ExecuteBsi(action,engineOff));
                        if(!ack.SequenceEqual(new[]{0x7B,0xB9}))throw new IOException("BSI nepotvrdila zápis: "+BsiServiceProfile.Hex(ack)+". Povel se neopakuje.");
                        if(!Read(wire,0xB9).SequenceEqual(expected))throw new IOException("Zpětný odečet se liší od požadovaného nastavení; žádný další zápis nebude proveden.");
                        // Reopen a fresh diagnostic session, then verify the complete record again.
                        Discovery.Close(wire,route,true);opened=false;opened=true;Open(wire,route);
                        if(!Read(wire,0xB9).SequenceEqual(expected))throw new IOException("Po novém připojení se nastavení nepotvrdilo.");
                        result.Changed=true;outcome="Denní svícení je v konfiguraci "+(enabled?"ZAPNUTÉ (původní typ)":"VYPNUTÉ (bez denního svícení)")+". BSI potvrdila zápis i celý kontrolní odečet po novém připojení. Měněny jsou pouze typ a výchozí zapnutí denního svícení. Chování světel ověřte po novém startu.";
                    }
                }
                result.Success=true;
            }catch(Exception e){result.Success=false;outcome=e.Message+(writeAttempted?"\r\nZměnový povel byl odeslán nebo se odesílal; výsledek není potvrzený. Bez automatického opakování či vracení zápisu.":"\r\nŽádný změnový povel nebyl odeslán.");}
            finally{if(opened){try{Discovery.Close(wire,route,true);result.Disconnected=true;}catch(Exception e){result.Success=false;outcome+="\r\nUkončení spojení nebylo potvrzené: "+e.Message;}}}
            result.Report=outcome;log(outcome);return result;
        }
        static void RequireRoutine(int[] b){if(b.Length<2||b[0]!=0x71||b[1]!=0xAD)throw new IOException("BSI odmítla rutinu: "+BsiServiceProfile.Hex(b));}
        internal static BsiServiceResult Run(Catalogue c,VehicleDefinition v,BsiAction action,bool engineOff,Action<string> progress,int intervalKm=10000)
        {
            var route=Route(c,v);string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs");Directory.CreateDirectory(folder);
            string path=Path.Combine(folder,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-bsi-"+action+".txt");BsiServiceResult result;
            using(var log=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read),Encoding.UTF8)){
                log.AutoFlush=true;Action<string> record=s=>{log.WriteLine(DateTimeOffset.Now.ToString("o")+" "+s);progress(s);};
                record("BSI SERVICE "+action+" / engine-off confirmed by user; one attempt, no retry.");
                try{using(var wire=new SerialVci(record))result=Session(wire,route,action,engineOff,record,intervalKm);}
                catch(Exception e){result=new BsiServiceResult{Report=e.Message};record(result.Report);}
            }
            result.LogPath=path;return result;
        }
    }
}
