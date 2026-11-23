using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PsaReader
{
    internal static class UniversalTests
    {
        static int count;
        static void Check(bool value,string name){if(!value)throw new Exception("FAIL universal: "+name);count++;}
        static void Reject(Action action,string name){bool failed=false;try{action();}catch(IOException){failed=true;}catch(InvalidOperationException){failed=true;}Check(failed,name);}
        internal static void Run()
        {
            var c=Catalogue.Load();int supported=0;
            foreach(var car in c.Vehicles){
                var routes=CanRoute.Plan(c,car);if(routes.Count>0)supported++;
                Check(routes.Select(r=>r.Id).Distinct().Count()==routes.Count,"unique routes "+car.Id);
                foreach(var route in routes){
                    Check(route.Candidates.All(k=>car.Nodes.Any(n=>n.Models.Any(m=>m.Key==k))),"only selected vehicle models "+car.Id+"/"+route.Id);
                    Check(route.Candidates.All(k=>c.Find(k).Addresses.Any(a=>a.Transport=="can-tp"&&String.IsNullOrEmpty(a.Bus)&&a.Request==route.Request&&a.Response==route.Response)),"only documented socket addresses");
                }
            }
            Check(supported>50,"multiple PSA architectures available");
            foreach(string id in new[]{"B5:c4","B5:picasso","B7:all"}){
                var car=c.Vehicles.Single(v=>v.Id==id);var routes=CanRoute.Plan(c,car);
                Check(routes.Count>10&&routes.Any(r=>r.Nodes.Contains("BSI"))&&routes.Any(r=>r.Nodes.Contains("CMM")),"C4 reading enabled "+id);
                Reject(()=>BsiServices.Route(c,car),"Phedra coding profile unavailable on "+id);
            }
            var uds=CanRoute.Plan(c,c.Vehicles.Single(v=>v.Id=="B7:all")).Single(r=>r.Id=="752:652");
            Check(uds.UseCabinPins&&!uds.CabinDiagnosticSession,"UDS BSI does not enter KWP coding session");
            foreach(var route in c.Vehicles.SelectMany(v=>CanRoute.Plan(c,v)).GroupBy(r=>r.Id).Select(g=>g.First())){
                var gate=new CommandSafety();string tx=route.Request.ToString("X3"),rx=route.Response.ToString("X3");
                string command="*66A_0_500_"+tx+"_"+rx+"_000_"+tx+"_01_3E_00_00_00_00_00d02_200"+(route.UseCabinPins?"H03L08":"#");
                gate.Send(command,delegate{});gate.AcceptResponse(command,"*256");
                gate.Send("*608_19_02_FF",delegate{});Check(true,"UDS DTC read permitted "+route.Id);
                foreach(string active in new[]{"*608_14_FF_FF_FF","*608_2E_F1_90_00","*608_27_83","*608_31_01_FF_00",ServiceMaintenance.ResetCommand})Reject(()=>gate.Send(active,delegate{}),"active denied without capability "+route.Id);
            }
            Check(AdapterIdentity.Number("*1513","FW")=="1513"&&AdapterIdentity.Number("*123456","serial")=="123456","different firmware and serial accepted as identity");
            Reject(()=>AdapterIdentity.Number("*0","FW"),"invalid firmware rejected");Reject(()=>AdapterIdentity.Number("*1423 1","FW"),"multi-number firmware rejected");
            Check(AdapterIdentity.SelectPort(new[]{"COM6","COM4"},new[]{"COM6"})=="COM6","new adapter port selected, stale registry entry ignored");
            Reject(()=>AdapterIdentity.SelectPort(new[]{"COM6","COM4"},new[]{"COM6","COM4"}),"ambiguous adapters do not select arbitrarily");
            Reject(()=>AdapterIdentity.SelectPort(new[]{"COM6"},new string[0]),"disconnected adapter rejected");
            Check(ModuleReader.DecodeFaults(new EcuDefinition(),new[]{0x59,2,0xFF}).Contains("Počet záznamů závad: 0"),"UDS no matching DTC");
            Check(ModuleReader.DecodeFaults(new EcuDefinition(),new[]{0x59,2,0xFF,0x12,0x34,0x56,0x08}).Contains("123456"),"UDS 24-bit DTC and status retained");
            Reject(()=>ModuleReader.DecodeFaults(new EcuDefinition(),new[]{0x59,2,0xFF,0x12}),"truncated UDS is not empty memory");
            Reject(()=>ModuleReader.DecodeFaults(new EcuDefinition(),new[]{0x59,1,0xFF}),"wrong UDS subfunction rejected");
            string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tests","export-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
            try{
                File.WriteAllText(Path.Combine(folder,"sample.txt"),"TX *201\r\nRX *1513");File.WriteAllText(Path.Combine(folder,"sample.json"),"{\"VehicleId\":\"B5:c4\"}");File.WriteAllText(Path.Combine(folder,"skip.exe"),"not a log");
                string archive=Path.Combine(folder,"bundle.zip");DiagnosticsExport.Create(archive,c,c.Vehicles.Single(v=>v.Id=="B5:c4"),folder);
                using(var zip=ZipFile.OpenRead(archive)){
                    Check(zip.Entries.Count==3&&zip.GetEntry("logs/skip.exe")==null,"export includes manifest and logs only");
                    using(var text=new StreamReader(zip.GetEntry("INFO.txt").Open())){var value=text.ReadToEnd();Check(value.Contains("B5:c4")&&value.Contains(DiagnosticsExport.BuildId)&&value.Contains(c.Revision),"export identifies selected profile, exact build and database");}
                    using(var text=new StreamReader(zip.GetEntry("logs/sample.txt").Open()))Check(text.ReadToEnd().Contains("RX *1513"),"raw adapter replies preserved in zip");
                }
            }finally{Directory.Delete(folder,true);}
            Console.WriteLine("PASS: "+count+" universal profile, transport protection and ZIP checks; "+supported+" profiles have CAN read routes. No device IO.");
        }
    }
}
