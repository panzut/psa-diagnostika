using System;
using System.IO;
using System.Linq;

namespace PsaReader
{
    // B4/C9 offsets are valid ONLY with these exact runtime maps and the bound
    // BSI identity/software. Source: PSA AEE2004 VCAN B4/C9 mapped definitions.
    internal static class ServiceMaintenance
    {
        internal const string ResetCommand="*608_31_AA_00_01";
        internal static readonly int[] ConfigMap={0,14,5,15,16};
        internal static readonly int[] StatusMap={62,30,43,44,46,48,63,65,27,28,35,36,38,45,0,2,20,31,49,66,67,68};
        internal static void ValidateKm(int km){if(km<1000||km>50000||km%200!=0)throw new IOException("Interval musí být 1 000 až 50 000 km, v násobcích 200 km.");}
        internal static int[] Configuration(int[] original,int km)
        {ValidateKm(km);BsiServiceProfile.Record(original,0xB4,5);var b=(int[])original.Clone();b[3]=km/200;return b;}
        internal static string ConfigurationCommand(int[] original,int km)
        {return "*608_3B_B4_FF_"+String.Join("_",Configuration(original,km).Skip(2).Select(x=>x.ToString("X2")));}
        internal static int Distance(int[] status)
        {BsiServiceProfile.Record(status,0xC9,25);return (status[12]*256+status[13])*20*((status[26]&64)==0?1:-1);}
        static void Verify(int[] config,int[] status,int[] expected,int km)
        {
            if(!config.SequenceEqual(expected))throw new IOException("Kontrolní odečet servisního nastavení nesouhlasí.");
            if(Distance(status)!=km)throw new IOException("Po resetu BSI hlásí "+Distance(status)+" km do servisu, požadováno "+km+" km. Další reset se neodesílá.");
        }
        static void LeaveMapSession(IBsiServiceVci wire,CanRoute route)
        {
            if(!Discovery.EcuBytes(wire.CabinSession(route,false)).SequenceEqual(new[]{0x50,0x81})||!Discovery.EcuBytes(wire.CabinSession(route,true)).SequenceEqual(new[]{0x50,0xC0}))throw new IOException("BSI nepotvrdila novou relaci.");
            if(BsiServiceProfile.Hex(BsiServices.Read(wire,0x80))!=BsiServiceProfile.Identity||BsiServiceProfile.Hex(BsiServices.Read(wire,0xFE))!=BsiServiceProfile.Software)throw new IOException("Identifikace nové relace nesouhlasí.");
        }
        internal static BsiServiceResult Session(IBsiServiceVci wire,CanRoute route,int km,bool engineOff,Action<string> log)
        {
            var result=new BsiServiceResult();bool opened=false,attempted=false,configured=false;string outcome=null;
            try{
                ValidateKm(km);
                if(!engineOff)throw new IOException("Nejdříve vypněte motor a ponechte zapnuté zapalování.");
                opened=true;BsiServices.Open(wire,route);
                BsiServices.Map(wire,route,0xB0);BsiServices.Map(wire,route,0xC0);
                var before=BsiServices.Read(wire,0xB4);var status=BsiServices.Read(wire,0xC9);var lights=BsiServices.Read(wire,0xB9);
                var expected=Configuration(before,km);
                log("BACKUP B4 "+BsiServiceProfile.Hex(before));log("BACKUP C9 "+BsiServiceProfile.Hex(status));log("BACKUP B9 "+BsiServiceProfile.Hex(lights));
                log("Servisní interval: "+before[3]*200+" → "+km+" km. Počítadlo před resetem: "+Distance(status)+" km; počet servisů: "+status[25]+".");
                log("EXPECTED B4 "+BsiServiceProfile.Hex(expected));
                // End map upload, open a fresh diagnostic session before any mutation.
                LeaveMapSession(wire,route);
                if(!BsiServices.Read(wire,0xB9).SequenceEqual(lights)||!BsiServices.Read(wire,0xB4).SequenceEqual(before))throw new IOException("Konfigurace se během přípravy změnila.");
                BsiServiceProfile.Power(BsiServices.Read(wire,0xC0),true);
                if(!before.SequenceEqual(expected)){
                    wire.UnlockBsiConfiguration(engineOff);
                    if(!BsiServices.Read(wire,0xB4).SequenceEqual(before))throw new IOException("Servisní konfigurace se změnila během odemykání.");
                    BsiServices.Read(wire,0xC9);BsiServiceProfile.Power(BsiServices.Read(wire,0xC0),true);
                    attempted=true;var ack=Discovery.EcuBytes(wire.SetServiceInterval(km,engineOff));
                    if(!ack.SequenceEqual(new[]{0x7B,0xB4}))throw new IOException("BSI nepotvrdila interval: "+BsiServiceProfile.Hex(ack));
                    if(!BsiServices.Read(wire,0xB4).SequenceEqual(expected))throw new IOException("Zapsaný interval neodpovídá kontrolnímu odečtu.");
                    configured=true;result.Changed=true;log("Interval "+km+" km potvrzen zpětným čtením.");
                    // Real 19:22 capture: B4 write succeeded, but AA in the same
                    // coding connection returned 7F 31 21. Keep reset separate from
                    // coding; close/wait/reopen and verify persistence before reset.
                    // This addresses that sequence; it is not proof of the ECU's
                    // internal busy cause. Never retry an ambiguous reset response.
                    log("Ukončuji konfiguraci. Reset proběhne v novém spojení po ověření uloženého intervalu.");
                    Discovery.Close(wire,route,true);opened=false;opened=true;BsiServices.Open(wire,route);
                    if(!BsiServices.Read(wire,0xB4).SequenceEqual(expected))throw new IOException("Po novém připojení se uložený interval nepotvrdil. Reset nebyl odeslán.");
                    BsiServices.Map(wire,route,0xB0);BsiServices.Map(wire,route,0xC0);LeaveMapSession(wire,route);
                }
                if(!BsiServices.Read(wire,0xB9).SequenceEqual(lights))throw new IOException("Před resetem se změnilo nastavení světel.");
                if(!BsiServices.Read(wire,0xB4).SequenceEqual(expected))throw new IOException("Interval před resetem nesouhlasí.");
                BsiServices.Read(wire,0xC9);BsiServiceProfile.Power(BsiServices.Read(wire,0xC0),true);
                // Exact CAN BSI procedure from 9psa_citro_service, selected by
                // 8psa_service_styrprot. One invocation; no guessed routine polling.
                attempted=true;var reset=Discovery.EcuBytes(wire.ExecuteBsi(BsiAction.ServiceReset,engineOff));
                if(reset.SequenceEqual(new[]{0x7F,0x31,0x21}))throw new IOException("BSI je zaneprázdněná (7F 31 21); reset nepřijala. Interval je "+km+" km. Reset se automaticky neopakuje.");
                if(reset.Length<2||reset[0]!=0x71||reset[1]!=0xAA)throw new IOException("BSI nepotvrdila reset servisu: "+BsiServiceProfile.Hex(reset));
                var after=BsiServices.Read(wire,0xC9);log("AFTER C9 "+BsiServiceProfile.Hex(after));
                Verify(BsiServices.Read(wire,0xB4),after,expected,km);
                if(!BsiServices.Read(wire,0xB9).SequenceEqual(lights))throw new IOException("Nastavení světel se změnilo; nutná kontrola.");
                Discovery.Close(wire,route,true);opened=false;opened=true;BsiServices.Open(wire,route);
                after=BsiServices.Read(wire,0xC9);Verify(BsiServices.Read(wire,0xB4),after,expected,km);
                if(!BsiServices.Read(wire,0xB9).SequenceEqual(lights))throw new IOException("Nastavení světel po připojení nesouhlasí.");
                result.Success=result.Changed=true;
                outcome="Servis resetován. Interval i vzdálenost do servisu: "+km.ToString("N0")+" km, ověřeno také po novém připojení. Počet servisů: "+status[25]+" → "+after[25]+". Časové nastavení zachováno.";
            }catch(Exception e){outcome=e.Message+(configured?"\r\nInterval již byl změněn a ověřen; reset není potvrzený.":attempted?"\r\nZměnový povel se odesílal; výsledek není potvrzený.":"\r\nŽádný změnový povel nebyl odeslán.")+" Bez automatického opakování.";}
            finally{if(opened){try{Discovery.Close(wire,route,true);result.Disconnected=true;}catch(Exception e){result.Success=false;outcome+="\r\nUkončení spojení nepotvrzeno: "+e.Message;}}}
            result.Report=outcome;log(outcome);return result;
        }
    }
}
