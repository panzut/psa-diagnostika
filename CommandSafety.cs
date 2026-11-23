using System;
using System.Globalization;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace PsaReader
{
    // This gate is on the single serial-write path, not in a window or imported catalogue.
    // No data file, checkbox or raw command can grant permission to an active operation.
    internal sealed class CommandSafety
    {
        sealed class Proof {internal int[] Bytes;internal long Time;}
        readonly Dictionary<string,Proof> faultProofs=new Dictionary<string,Proof>();
        FaultClearPlan clearPlan;string clearArmed;
        void CheckClear(FaultClearPlan plan,bool engineOff)
        {
            plan.Validate();
            if(!engineOff||requestId!=plan.Route.Request||cabinPins!=plan.Route.UseCabinPins||requestId==0x744)throw new InvalidOperationException("Mazání nemá potvrzený cíl a podmínky.");
            Proof id,dtc;if(!faultProofs.TryGetValue(plan.IdentityCommand,out id)||!faultProofs.TryGetValue(plan.ReadCommand,out dtc)||clock.ElapsedMilliseconds-id.Time>8000||clock.ElapsedMilliseconds-dtc.Time>8000||!plan.ValidFaultReply(dtc.Bytes))throw new InvalidOperationException("Chybí čerstvá identifikace a přečtená paměť závad.");
            plan.VerifyIdentity(id.Bytes);
        }
        internal string ArmFaultClear(FaultClearPlan plan,bool engineOff)
        {clearArmed=null;CheckClear(plan,engineOff);clearPlan=plan;clearArmed=plan.ClearCommand;return clearArmed;}
        int requestId=-1; bool cabinPins;
        bool session,identity,software,lightingMap,powerMap,economyStarted,configurationUnlocked,serviceConfigMap,serviceStatusMap;
        int[] serviceConfig,serviceStatus;long serviceConfigTime=-100000,serviceStatusTime=-100000;
        bool serviceCodingInConnection;
        void CheckService(bool engineOff)
        {
            CheckBsiConditions(engineOff);
            if(!serviceConfigMap||!serviceStatusMap||clock.ElapsedMilliseconds-serviceConfigTime>8000||clock.ElapsedMilliseconds-serviceStatusTime>8000)throw new InvalidOperationException("Chybí aktuální ověření servisních parametrů BSI.");
            BsiServiceProfile.Record(serviceConfig,0xB4,5);BsiServiceProfile.Record(serviceStatus,0xC9,25);
        }
        internal void ArmServiceInterval(int km,bool engineOff)
        {
            armed=null;CheckService(engineOff);
            if(!configurationUnlocked)throw new InvalidOperationException("BSI nepotvrdila přístup ke konfiguraci.");
            armed=ServiceMaintenance.ConfigurationCommand(serviceConfig,km);
        }
        string expectedKey;long seedTime;
        internal void ArmBsiConfiguration(bool engineOff)
        {
            armed=null;CheckBsiConditions(engineOff);
            if(!lightingMap||clock.ElapsedMilliseconds-lightingTime>8000)throw new InvalidOperationException("Chybí aktuální ověřené nastavení B9.");
            armed="*608_27_83";
        }
        void CheckBsiConditions(bool engineOff)
        {
            if(!engineOff||requestId!=0x752||!cabinPins||!session||!identity||!software||!powerMap||clock.ElapsedMilliseconds-powerTime>8000)
                throw new InvalidOperationException("Chybí aktuální ověření BSI, zapalování nebo potvrzení vypnutého motoru.");
            BsiServiceProfile.Power(power,true);
        }
        internal bool ConfigurationUnlocked {get{return configurationUnlocked;}}
        internal string ConfigurationKeyCommand(){if(expectedKey==null||clock.ElapsedMilliseconds-seedTime>4000)throw new InvalidOperationException("Chybí čerstvá výzva BSI.");return expectedKey;}
        string armed,pending;int[] lighting,power;
        readonly List<DataRead> maps=new List<DataRead>();
        readonly Stopwatch clock=Stopwatch.StartNew();long lightingTime=-100000,powerTime=-100000,economyTime;
        internal void ArmBsi(BsiAction action,bool engineOff)
        {
            armed=null;
            CheckBsiConditions(engineOff);
            if(action==BsiAction.EconomyOff){
                if((power[30]&4)==0)throw new InvalidOperationException("Úsporný režim již není aktivní.");
                armed=BsiServiceProfile.EconomyStart;
            }else if(action==BsiAction.ServiceReset){
                if(serviceCodingInConnection)throw new InvalidOperationException("Po změně intervalu je nutné ukončit spojení a znovu ověřit BSI před resetem.");
                CheckService(engineOff);armed=ServiceMaintenance.ResetCommand;
            }else if(action==BsiAction.DaylightOff||action==BsiAction.DaylightOn){
                if(!configurationUnlocked)throw new InvalidOperationException("BSI nepotvrdila přístup ke konfiguraci.");
                if(!lightingMap||clock.ElapsedMilliseconds-lightingTime>8000)throw new InvalidOperationException("Chybí čerstvý odečet a tabulka nastavení světel.");
                if(BsiServiceProfile.Daylight(lighting,action==BsiAction.DaylightOn).SequenceEqual(lighting))throw new InvalidOperationException("Nastavení už odpovídá požadavku.");
                armed=BsiServiceProfile.DaylightCommand(lighting,action==BsiAction.DaylightOn);
            }else throw new InvalidOperationException("Neznámá servisní funkce.");
        }
        void ClearBsi()
        {session=identity=software=lightingMap=powerMap=economyStarted=configurationUnlocked=serviceConfigMap=serviceStatusMap=serviceCodingInConnection=false;armed=expectedKey=null;lighting=power=serviceConfig=serviceStatus=null;maps.Clear();lightingTime=powerTime=serviceConfigTime=serviceStatusTime=-100000;}
        internal string ArmedBsiCommand(){if(armed==null)throw new InvalidOperationException("Žádný ověřený servisní povel není připravený.");return armed;}
        static readonly Regex setup=new Regex(@"^\*66A_0_500_([0-9A-F]{3})_([0-9A-F]{3})_000_\1_01_3E_00_00_00_00_00d02_200(#|H03L08)$");
        static readonly Regex readLocal=new Regex(@"^\*608_21_[0-9A-F]{2}$");
        static readonly Regex readCommon=new Regex(@"^\*608_22_[0-9A-F]{2}_[0-9A-F]{2}$");
        static readonly Regex map=new Regex(@"^\*608_35_(B0|C0|D0)_00_00_([0-9A-F]{2})_11$");
        internal const string ActiveUnavailable="Akční test ani zápis nebyl odeslán: chybí ověřený profil provedení a ukončení.";
        internal void Send(string command,Action<string> write)
        {
            Validate(command);
            pending=command;
            write(command);
        }
        void Validate(string command)
        {
            if(String.IsNullOrEmpty(command)||command.IndexOfAny(new[]{'\r','\n','\0'})>=0)throw new InvalidOperationException("Neplatný příkaz.");
            string clear=clearArmed;clearArmed=null;
            if(command==clear){CheckClear(clearPlan,true);faultProofs.Clear();clearPlan=null;return;}
            if(command=="*607"||command.StartsWith("*66A_")||command.StartsWith("*608_10_")||command.StartsWith("*608_27_")||command.StartsWith("*608_3B_")){faultProofs.Clear();clearPlan=null;}
            if(readLocal.IsMatch(command)||readCommon.IsMatch(command)||command=="*608_17_FF_00"||command=="*608_19_02_FF")faultProofs.Remove(command);
            if(command=="*607"){requestId=-1;cabinPins=false;ClearBsi();return;}
            if(command=="*201"||command=="*203"||command=="*20A"||command=="*200"||command=="*60b"||command=="*60bc")return;
            var cfg=setup.Match(command);
            if(cfg.Success){
                requestId=-1;cabinPins=false;ClearBsi();
                int tx=Int32.Parse(cfg.Groups[1].Value,NumberStyles.HexNumber),rx=Int32.Parse(cfg.Groups[2].Value,NumberStyles.HexNumber);
                if(!CanRoute.IsReviewed(tx,rx)||(cfg.Groups[3].Value=="H03L08"&&tx<0x740))throw new InvalidOperationException("Nepovolená diagnostická cesta.");
                return;
            }
            if(requestId<0)throw new InvalidOperationException("Chybí potvrzená diagnostická cesta.");
            // A capability is single-use and invalidated by intervening ECU commands.
            string permitted=armed;armed=null;
            if(permitted!=null&&command==permitted&&requestId==0x752&&cabinPins&&session&&identity&&software){
                if(clock.ElapsedMilliseconds-powerTime>8000)throw new InvalidOperationException("Kontrola napájení vypršela.");
                if(command==ServiceMaintenance.ResetCommand||command.StartsWith("*608_3B_B4_"))CheckService(true);
                if(command.StartsWith("*608_3B_B4_"))serviceCodingInConnection=true;
                if(command=="*608_27_83"){configurationUnlocked=false;expectedKey=null;return;}
                lightingTime=powerTime=serviceConfigTime=serviceStatusTime=-100000;economyStarted=false;return;
            }
            string key=expectedKey;expectedKey=null;
            if(command==key&&requestId==0x752&&cabinPins&&session&&identity&&software&&clock.ElapsedMilliseconds-seedTime<4000)return;
            if(command==BsiServiceProfile.EconomyPoll&&requestId==0x752&&cabinPins&&session&&economyStarted&&clock.ElapsedMilliseconds-economyTime<8000){economyStarted=false;return;}
            if(command=="*608_10_C0"||command=="*608_10_81"){
                // Parameter tables describe this hardware/software, not session state.
                // Retain them only while the exact same physical route stays selected;
                // identity, software, values and security must all be verified anew.
                session=identity=software=economyStarted=configurationUnlocked=false;
                armed=expectedKey=null;lighting=power=serviceConfig=serviceStatus=null;lightingTime=powerTime=serviceConfigTime=serviceStatusTime=-100000;
            }
            if(command=="*608_21_80")identity=false;
            if(command=="*608_21_FE")software=false;
            if(command=="*608_21_B9"){lighting=null;lightingTime=-100000;}
            if(command=="*608_21_C0"){power=null;powerTime=-100000;}
            if(command=="*608_21_B4"){serviceConfig=null;serviceConfigTime=-100000;}
            if(command=="*608_21_C9"){serviceStatus=null;serviceStatusTime=-100000;}
            if(command.StartsWith("*608_35_B0_"))lightingMap=serviceConfigMap=false;
            if(command.StartsWith("*608_35_C0_"))powerMap=serviceStatusMap=false;
            if(command=="*608f00_0A"||command=="*608pN")return;
            // SRS permits the existing bounded reads and diagnostic session only.
            // Neither programming/unlock nor actuator services are classified as reads.
            if(readLocal.IsMatch(command)||readCommon.IsMatch(command)||command=="*608_17_FF_00"||command=="*608_19_02_FF")return;
            if(requestId>=0x740&&(command=="*608_10_C0"||command=="*608_10_81"))return;
            if(requestId<0x740&&command=="*608_81")return;
            var table=map.Match(command);
            if(table.Success&&requestId==0x752&&cabinPins){
                int page=Int32.Parse(table.Groups[2].Value,NumberStyles.HexNumber);
                if(page>=1&&page<=32)return;
            }
            if(requestId==0x744)throw new InvalidOperationException("SRS: aktivace, zápis, odemykání a reset jsou trvale zakázány.");
            throw new InvalidOperationException(ActiveUnavailable);
        }
        internal void AcceptResponse(string command,string response)
        {
            var cfg=setup.Match(command);
            if(command!=pending)return;pending=null;
            if(requestId>=0&&(readLocal.IsMatch(command)||readCommon.IsMatch(command)||command=="*608_17_FF_00"||command=="*608_19_02_FF")){
                try{var b=Discovery.EcuBytes(response);if(b.Length>=2&&b[0]!=0x7F&&b[0]!=0xFF)faultProofs[command]=new Proof{Bytes=b,Time=clock.ElapsedMilliseconds};}catch(Exception){faultProofs.Remove(command);}
            }
            if(!cfg.Success){
                if(requestId!=0x752||!cabinPins)return;
                try{
                    var bytes=Discovery.EcuBytes(response);
                    if(command=="*608_10_C0")session=bytes.SequenceEqual(new[]{0x50,0xC0});
                    if(!session)return;
                    if(command=="*608_21_80")identity=BsiServiceProfile.Hex(bytes)==BsiServiceProfile.Identity;
                    if(command=="*608_21_FE")software=BsiServiceProfile.Hex(bytes)==BsiServiceProfile.Software;
                    if(command=="*608_21_B9"){BsiServiceProfile.Record(bytes,0xB9,22);lighting=bytes;lightingTime=clock.ElapsedMilliseconds;}
                    if(command=="*608_21_C0"){BsiServiceProfile.Record(bytes,0xC0,30);power=bytes;powerTime=clock.ElapsedMilliseconds;}
                    if(command=="*608_21_B4"){BsiServiceProfile.Record(bytes,0xB4,5);serviceConfig=bytes;serviceConfigTime=clock.ElapsedMilliseconds;}
                    if(command=="*608_21_C9"){BsiServiceProfile.Record(bytes,0xC9,25);serviceStatus=bytes;serviceStatusTime=clock.ElapsedMilliseconds;}
                    if(command=="*608_27_83"){
                        configurationUnlocked=false;expectedKey=null;
                        if(bytes.Length==6&&bytes[0]==0x67&&bytes[1]==0x83){var seed=bytes.Skip(2).ToArray();if(seed.All(x=>x==0))configurationUnlocked=true;else{expectedKey=BsiConfigurationKey.Command(seed);seedTime=clock.ElapsedMilliseconds;}}
                    }
                    if(command.StartsWith("*608_27_84_"))configurationUnlocked=bytes.SequenceEqual(new[]{0x67,0x84});
                    if(command==BsiServiceProfile.EconomyStart){economyStarted=bytes.Length>=2&&bytes[0]==0x71&&bytes[1]==0xAD;economyTime=clock.ElapsedMilliseconds;}
                    var m=map.Match(command);
                    if(m.Success){
                        int root=Convert.ToInt32(m.Groups[1].Value,16),page=Convert.ToInt32(m.Groups[2].Value,16);
                        if(root!=0xB0&&root!=0xC0)return;
                        if(page==1)maps.RemoveAll(p=>p.Identifier==root);
                        maps.Add(new DataRead{Service=0x35,Identifier=root,Page=page,State="Přečteno",Raw=BsiServiceProfile.Hex(bytes)});
                        if(bytes.Length>=3&&bytes[2]==0xFF){
                            BsiServiceProfile.CheckMap(maps,root,root==0xB0?0xB9:0xC0,root==0xB0?BsiServiceProfile.LightingMap:BsiServiceProfile.PowerMap);if(root==0xB0)lightingMap=true;else powerMap=true;
                            BsiServiceProfile.CheckMap(maps,root,root==0xB0?0xB4:0xC9,root==0xB0?ServiceMaintenance.ConfigMap:ServiceMaintenance.StatusMap);if(root==0xB0)serviceConfigMap=true;else serviceStatusMap=true;
                        }
                    }
                }catch(Exception){armed=null;}
                return;
            }
            requestId=-1;cabinPins=false;
            if(response!="*256")return;
            int tx=Int32.Parse(cfg.Groups[1].Value,NumberStyles.HexNumber),rx=Int32.Parse(cfg.Groups[2].Value,NumberStyles.HexNumber);
            if(!CanRoute.IsReviewed(tx,rx))return;
            requestId=tx;cabinPins=cfg.Groups[3].Value=="H03L08";
        }
    }
}
