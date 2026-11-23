using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace PsaReader
{
    public sealed class VehicleForm : Form
    {
        readonly Catalogue catalogue;
        readonly ComboBox vehicles=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
        readonly TextBox search=new TextBox{Dock=DockStyle.Fill};
        readonly DataGridView grid=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=Color.White,AutoGenerateColumns=false};
        readonly TextBox details=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=false,Font=new Font("Consolas",10)};
        readonly CheckBox parked=new CheckBox{Text="Auto stojí, zapalování je zapnuté, DS150E je připojená a Delphi je zavřené.",AutoSize=true};
        readonly Label status=new Label{Dock=DockStyle.Bottom,Height=44,Padding=new Padding(10),Text="Nepřipojeno. Vyberte auto; připojení začne až tlačítkem."};
        readonly Button discover=new Button{Text="Vyhledat jednotky",AutoSize=true};
        readonly Button abs=new Button{Text="Přečíst závady ABS",AutoSize=true};
        readonly Button stop=new Button{Text="Zastavit průchod",AutoSize=true,Enabled=false};
        readonly Button saved=new Button{Text="Poslední průchod",AutoSize=true};
        readonly Button probe=new Button{Text="Vyhledat jen vybranou",AutoSize=true};
        readonly Button faults=new Button{Text="Závady vybrané jednotky",AutoSize=true};
        readonly Button lastFaults=new Button{Text="Uložené závady",AutoSize=true};
        readonly Button snapshot=new Button{Text="Jednorázově přečíst hodnoty",AutoSize=true};
        readonly Button allData=new Button{Text="Přečíst všechna data auta",AutoSize=true};
        readonly Button nodeData=new Button{Text="Všechna data vybrané",AutoSize=true};
        readonly Button savedData=new Button{Text="Uložený úplný odečet",AutoSize=true};
        readonly Button openUnit=new Button{Text="Otevřít jednotku",AutoSize=true};
        readonly Button exportLogs=new Button{Text="Exportovat logy…",AutoSize=true};
        VehicleSnapshot latestData;
        readonly ComboBox records=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=530,DisplayMember="Name"};
        bool busy; volatile bool cancel; bool filtering;
        readonly Dictionary<string,DiscoveredEcu> observed=new Dictionary<string,DiscoveredEcu>();
        VehicleDefinition Vehicle { get { return vehicles.SelectedItem as VehicleDefinition; } }
        VehicleNode Node { get { return grid.SelectedRows.Count==1?grid.SelectedRows[0].Tag as VehicleNode:null; } }
        public VehicleForm()
        {
            catalogue=Catalogue.Load();
            Text="PSA Diagnostika — vozidla a jednotky";Font=new Font("Segoe UI",10);Size=new Size(1240,800);MinimumSize=new Size(960,620);StartPosition=FormStartPosition.CenterScreen;
            var heading=new Label{Dock=DockStyle.Top,Height=64,Padding=new Padding(12),Text="PSA Diagnostika  |  DS150E\r\nDvojklikem otevři jednotku · přehled, závady a hodnoty přímo v aplikaci",Font=new Font("Segoe UI",11)};
            var selection=new TableLayoutPanel{Dock=DockStyle.Top,Height=76,ColumnCount=2,RowCount=2,Padding=new Padding(10,3,10,3)};
            selection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,110));selection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            selection.Controls.Add(new Label{Text="Hledat auto",AutoSize=true},0,0);selection.Controls.Add(search,1,0);
            selection.Controls.Add(new Label{Text="Profil vozidla",AutoSize=true},0,1);selection.Controls.Add(vehicles,1,1);
            var toolbar=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(10),WrapContents=true};
            toolbar.Controls.Add(parked);toolbar.SetFlowBreak(parked,true);
            var warning=new Label{Text="Během diagnostiky nejezděte. ABS/ESP může být omezené; potvrzení krabičky neověřuje funkčnost ABS.",AutoSize=true,ForeColor=Color.DarkRed};
            toolbar.Controls.Add(warning);toolbar.SetFlowBreak(warning,true);
            foreach(var b in new[]{openUnit,discover,allData,savedData,stop,exportLogs}){b.Margin=new Padding(0,8,10,0);toolbar.Controls.Add(b);}
            exportLogs.Click+=delegate{using(var dialog=new SaveFileDialog{Filter="Diagnostické logy ZIP|*.zip",FileName="PSA-logy-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".zip",Title="Uložit logy k poslání — obsahují i VIN a předchozí odečty"})if(dialog.ShowDialog()==DialogResult.OK)try{DiagnosticsExport.Create(dialog.FileName,catalogue,Vehicle);status.Text="Logy uloženy: "+dialog.FileName+". Tento ZIP pošli k vyhodnocení.";}catch(Exception ex){DiagnosticsExport.RecordError(ex);MessageBox.Show(ex.Message,"Export logů se nepodařil");}};
            var export=new Button{Text="Uložit zobrazený přehled…",AutoSize=true,Margin=new Padding(0,8,0,0)};toolbar.Controls.Add(export);
            var split=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterWidth=7};
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="node",HeaderText="Jednotka",FillWeight=25});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="models",HeaderText="Možné modely podle databáze",FillWeight=38});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="state",HeaderText="Výsledek čtení",FillWeight=37});
            split.Panel1.Controls.Add(grid);split.Panel2.Controls.Add(details);
            Controls.Add(split);Controls.Add(toolbar);Controls.Add(selection);Controls.Add(heading);Controls.Add(status);
            Shown+=delegate{split.SplitterDistance=Math.Max(160,split.Height*2/3);BeginInvoke(new Action(LoadData));};
            search.TextChanged+=delegate{Filter();};vehicles.SelectedIndexChanged+=delegate{if(!filtering)SelectVehicle();};grid.SelectionChanged+=delegate{ShowNode();};
            openUnit.Click+=delegate{OpenUnit();};grid.CellDoubleClick+=delegate(object sender,DataGridViewCellEventArgs e){if(e.RowIndex>=0)OpenUnit();};grid.KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Enter){e.Handled=true;e.SuppressKeyPress=true;OpenUnit();}};
            discover.Click+=delegate{BeginRead(true);};abs.Click+=delegate{BeginRead(false);};
            probe.Click+=delegate{if(Node!=null)BeginRead(true,Node.Code);};
            faults.Click+=delegate{BeginModule(false);};snapshot.Click+=delegate{BeginModule(true);};records.SelectedIndexChanged+=delegate{snapshot.Enabled=!busy&&records.SelectedItem!=null;};
            lastFaults.Click+=delegate{LoadFaults();};
            allData.Click+=delegate{BeginAll(null);};nodeData.Click+=delegate{if(Node!=null)BeginAll(Node.Code);};savedData.Click+=delegate{LoadData();};
            stop.Click+=delegate{cancel=true;stop.Enabled=false;status.Text="Zastavím po ukončení právě zkoušené adresy.";};
            saved.Click+=delegate{LoadLast();};
            export.Click+=delegate{using(var d=new SaveFileDialog{Filter="Textový přehled|*.txt",FileName="psa-prehled.txt"})if(d.ShowDialog()==DialogResult.OK)try{File.WriteAllText(d.FileName,details.Text,Encoding.UTF8);}catch(Exception e){MessageBox.Show(e.Message);}};
            FormClosing+=delegate(object sender,FormClosingEventArgs e){if(busy){e.Cancel=true;cancel=true;status.Text="Dokončuji ukončení spojení. Poté lze okno zavřít.";}};
            Filter();
            vehicles.SelectedItem=catalogue.Vehicles.FirstOrDefault(v=>v.Id=="VCAN:all");
        }
        void Filter()
        {
            string id=Vehicle==null?"VCAN:all":Vehicle.Id;filtering=true;vehicles.Items.Clear();
            foreach(var v in catalogue.Vehicles.Where(v=>v.Title.IndexOf(search.Text,StringComparison.OrdinalIgnoreCase)>=0||v.Code.IndexOf(search.Text,StringComparison.OrdinalIgnoreCase)>=0))vehicles.Items.Add(v);
            vehicles.SelectedItem=vehicles.Items.Cast<VehicleDefinition>().FirstOrDefault(v=>v.Id==id);
            if(vehicles.SelectedIndex<0&&vehicles.Items.Count>0)vehicles.SelectedIndex=0;
            filtering=false;SelectVehicle();
        }
        void SelectVehicle()
        {
            observed.Clear();latestData=null;parked.Checked=false;grid.Rows.Clear();
            if(Vehicle==null){details.Text="Žádný odpovídající profil.";discover.Enabled=false;UpdateAbs();UpdateModule();return;}
            foreach(var n in Vehicle.Nodes){int row=grid.Rows.Add(n.Name+" ("+n.Code+")",String.Join(", ",(n.Models??new EcuReference[0]).Select(m=>m.Name)),"Nezkoušeno" );grid.Rows[row].Tag=n;}
            discover.Enabled=CanRoute.Plan(catalogue,Vehicle).Count>0;allData.Enabled=nodeData.Enabled=discover.Enabled;UpdateAbs();ShowNode();
            status.Text=catalogue.Vehicles.Length+" variant vozů · "+catalogue.Ecus.Length+" definic ECU · "+(CanRoute.Plan(catalogue,Vehicle).Count>0?"Testovací čtení: "+CanRoute.Plan(catalogue,Vehicle).Count+" diagnostických adres.":"Pro tento profil zatím chybí ovladač spojení; dostupná je databáze.");
        }
        void UpdateAbs(){abs.Enabled=!busy&&Vehicle!=null&&Vehicle.Id=="VCAN:all"&&Node!=null&&Node.Code=="ABS";probe.Enabled=!busy&&Vehicle!=null&&CanRoute.Supports(Vehicle)&&Node!=null&&CanRoute.Plan(catalogue,Vehicle).Any(r=>r.Nodes.Contains(Node.Code));}
        IdentityMatch SelectedMatch(out DiscoveredEcu unit)
        {
            unit=null;if(Vehicle==null||Node==null||!CanRoute.Supports(Vehicle))return null;
            if(latestData!=null){var found=latestData.Units.Where(u=>u.Unit.Responded&&u.Unit.Disconnected&&u.Unit.Nodes.Contains(Node.Code)).ToArray();if(found.Length==1){var exact=found[0].Matches.Where(m=>Node.Models.Any(model=>model.Key==m.Key)).ToArray();if(exact.Length==1){unit=found[0].Unit;return exact[0];}}}
            var hits=observed.Values.Where(u=>u.Responded&&u.Disconnected&&u.Nodes.Contains(Node.Code)).ToArray();if(hits.Length!=1)return null;
            var matches=IdentityResolver.Resolve(catalogue,Vehicle.Code,hits[0]).Where(m=>Node.Models.Any(model=>model.Key==m.Key)).ToList();if(matches.Count!=1)return null;
            if(!Node.Models.Any(m=>m.Key==matches[0].Key))return null;unit=hits[0];return matches[0];
        }
        void UpdateModule()
        {
            var previous=records.SelectedItem as ReadRecord;
            records.Items.Clear();faults.Enabled=snapshot.Enabled=false;records.Enabled=!busy;lastFaults.Enabled=!busy;
            DiscoveredEcu unit;var match=SelectedMatch(out unit);if(match==null)return;
            var d=catalogue.Definition(match.Key);faults.Enabled=!busy&&(d.ReadDtcServices??new int[0]).Any(service=>service==0x17||service==0x19);
            foreach(var record in d.Records.Where(r=>r.Kind!="config"))try{var canonical=ModuleReader.CanonicalRecord(d,record,match.Variant);if(IdentityResolver.Layout(canonical,match.Variant).Length.HasValue)records.Items.Add(canonical);}catch(IOException){}
            if(records.Items.Count>0){records.SelectedItem=records.Items.Cast<ReadRecord>().FirstOrDefault(r=>previous!=null&&r.Service==previous.Service&&r.Identifier==previous.Identifier);if(records.SelectedIndex<0)records.SelectedIndex=0;}
        }
        void ShowNode()
        {
            openUnit.Enabled=!busy&&Node!=null;
            UpdateAbs();UpdateModule();if(Node==null||Vehicle==null)return;
            if(latestData!=null){var units=latestData.Units.Where(u=>u.Unit.Nodes.Contains(Node.Code)).ToList();if(units.Count>0){details.Text=new VehicleSnapshot{Started=latestData.Started,Units=units,Completed=units.All(u=>u.Completed),Error=latestData.Error}.Report();return;}}
            string text=catalogue.Describe(Vehicle,Node);
            var hits=observed.Values.Where(u=>u.Nodes.Contains(Node.Code)).ToArray();
            if(hits.Any(u=>u.Responded&&u.Nodes.Length==1))text=text.Replace("Přítomnost zatím není potvrzená.","Odpověď na adresu je doložena uloženým průchodem.");
            if(hits.Length>0)text="POSLEDNÍ PRŮCHOD — uložené údaje, nikoli současné spojení\r\n"+String.Join("\r\n",hits.Select(u=>u.Address+" · "+u.State+"\r\n"+u.Identity+"\r\n"+u.Error))+"\r\n\r\n"+text;
            DiscoveredEcu selected;var match=SelectedMatch(out selected);if(match!=null)text="SHODA S DATABÁZÍ: "+match.Model+" / "+match.Variant+"\r\nSoftwarová reference: "+match.Reference+". Před čtením ji znovu ověřím.\r\n\r\n"+text;
            details.Text=text;
        }
        void ShowFound(DiscoveredEcu unit)
        {
            observed[unit.Address]=unit;
            var matches=latestData==null?IdentityResolver.Resolve(catalogue,Vehicle.Code,unit):latestData.Units.Where(u=>u.Unit.Address==unit.Address).SelectMany(u=>u.Matches).ToList();
            foreach(DataGridViewRow row in grid.Rows){var n=(VehicleNode)row.Tag;if(!unit.Nodes.Contains(n.Code))continue;var exact=matches.Where(m=>n.Models.Any(k=>k.Key==m.Key)).ToArray();row.Cells["state"].Value=!unit.Responded?"Bez potvrzené odpovědi":exact.Length==1?"Komunikace ověřena · "+exact[0].Model:unit.State.Contains("odmítla")?"Komunikace funguje · identifikační dotaz nepřijat":"Komunikace ověřena";row.DefaultCellStyle.ForeColor=!unit.Disconnected?Color.DarkRed:unit.Responded?Color.DarkGreen:Color.DarkSlateGray;}
        }
        void SetBusy(bool value)
        {
            exportLogs.Enabled=!value;
            busy=value;vehicles.Enabled=search.Enabled=parked.Enabled=saved.Enabled=grid.Enabled=!value;discover.Enabled=!value&&CanRoute.Plan(catalogue,Vehicle).Count>0;allData.Enabled=nodeData.Enabled=discover.Enabled;savedData.Enabled=!value;stop.Enabled=value;UpdateAbs();UpdateModule();
            openUnit.Enabled=!value&&Node!=null;
        }
        void OpenUnit()
        {
            if(busy||Node==null||Vehicle==null)return;
            var node=Node;var vehicle=Vehicle;var units=latestData==null?new List<UnitSnapshot>():latestData.Units.Where(u=>u.Unit.Nodes.Contains(node.Code)).ToList();
            using(var form=new UnitForm(catalogue,vehicle,node,units,result=>{
                if(latestData==null)latestData=result;
                else{foreach(var item in result.Units){latestData.Units.RemoveAll(old=>old.Unit.Address==item.Unit.Address);latestData.Units.Add(item);}latestData.Finished=result.Finished;}
                foreach(var item in result.Units)ShowFound(item.Unit);
            }))form.ShowDialog(this);
            ShowNode();
        }
        void BeginAll(string node)
        {
            if(busy||Vehicle==null||CanRoute.Plan(catalogue,Vehicle).Count==0)return;
            if(!parked.Checked){status.Text="Nejprve potvrďte, že auto stojí a je připravené ke čtení.";return;}
            var vehicle=Vehicle;cancel=false;latestData=null;SetBusy(true);
            var worker=new BackgroundWorker{WorkerReportsProgress=true};
            worker.DoWork+=delegate(object sender,DoWorkEventArgs e){e.Result=BulkReader.Run(catalogue,vehicle,()=>cancel,s=>worker.ReportProgress(0,s),u=>worker.ReportProgress(0,u),node);};
            worker.ProgressChanged+=delegate(object sender,ProgressChangedEventArgs e){var u=e.UserState as UnitSnapshot;if(u!=null)ShowFound(u.Unit);else status.Text=Convert.ToString(e.UserState);};
            worker.RunWorkerCompleted+=delegate(object sender,RunWorkerCompletedEventArgs e){SetBusy(false);parked.Checked=false;if(e.Error!=null){details.Text=e.Error.Message;status.Text="Odečet se nepodařil.";}else{latestData=(VehicleSnapshot)e.Result;details.Text=latestData.Report()+"\r\nZáznam: "+latestData.LogPath;status.Text="Uloženo "+latestData.Units.Sum(u=>u.Reads.Count(r=>r.State=="Přečteno"))+" čtecích odpovědí. "+(latestData.Completed?"Průchod dokončen.":"Některé položky nebyly dočteny — viz přehled.");}worker.Dispose();};worker.RunWorkerAsync();
        }
        void LoadData()
        {
            if(Vehicle==null)return;
            try{var dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs");if(!Directory.Exists(dir))return;
                var parser=new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=32*1024*1024};
                foreach(var path in Directory.GetFiles(dir,"*-all-data-*.txt.json").OrderByDescending(x=>x)){
                    var data=parser.Deserialize<VehicleSnapshot>(File.ReadAllText(path,Encoding.UTF8));if(data.VehicleId!=Vehicle.Id||data.Units==null||data.Units.Count==0)continue;
                    foreach(var item in data.Units){BulkReader.ResolveReferences(catalogue,Vehicle,item);MappedDecoder.Reprocess(catalogue,item);}
                    latestData=data;observed.Clear();foreach(var unit in data.Units)ShowFound(unit.Unit);UpdateModule();details.Text="ULOŽENÁ DATA — bez připojení\r\n"+data.Report();status.Text="Uložený odečet z "+data.Started;return;
                }status.Text="Zatím není uložený úplný odečet.";
            }catch(Exception e){status.Text="Odečet nelze načíst: "+e.Message;}
        }
        void BeginModule(bool values)
        {
            if(busy)return;
            if(!parked.Checked){status.Text="Nejprve potvrďte, že auto stojí a je připravené ke čtení.";return;}
            DiscoveredEcu unit;var match=SelectedMatch(out unit);if(match==null)return;
            var record=values?records.SelectedItem as ReadRecord:null;if(values&&record==null)return;var vehicle=Vehicle;
            SetBusy(true);stop.Enabled=false;
            var worker=new BackgroundWorker{WorkerReportsProgress=true};
            worker.DoWork+=delegate(object sender,DoWorkEventArgs e){e.Result=ModuleReader.Run(catalogue,vehicle,unit,match,record,s=>worker.ReportProgress(0,s));};
            worker.ProgressChanged+=delegate(object sender,ProgressChangedEventArgs e){status.Text=Convert.ToString(e.UserState);};
            worker.RunWorkerCompleted+=delegate(object sender,RunWorkerCompletedEventArgs e){SetBusy(false);parked.Checked=false;if(e.Error!=null){details.Text=e.Error.Message;status.Text="Čtení se nepodařilo.";}else{var r=(ReadResult)e.Result;details.Text=r.Report+"\r\nZáznam: "+r.LogPath;status.Text=r.Success?"Odečet dokončen a ukončení potvrzeno krabičkou.":"Čtení nebo ukončení nepotvrzeno — viz podrobnosti.";}worker.Dispose();};worker.RunWorkerAsync();
        }
        void BeginRead(bool scan,string node=null)
        {
            if(busy||Vehicle==null)return;
            if(!parked.Checked){status.Text="Nejprve potvrďte, že auto stojí a je připravené ke čtení.";return;}
            var selected=Vehicle;
            if(CanRoute.Plan(catalogue,selected).Count==0||(!scan&&(selected.Id!="VCAN:all"||Node==null||Node.Code!="ABS")))return;
            cancel=false;SetBusy(true);if(!scan)stop.Enabled=false;
            if(scan&&node==null){observed.Clear();foreach(DataGridViewRow row in grid.Rows){row.Cells["state"].Value="Nezkoušeno";row.DefaultCellStyle.ForeColor=Color.Black;}}
            var worker=new BackgroundWorker{WorkerReportsProgress=true};
            worker.DoWork+=delegate(object sender,DoWorkEventArgs e){
                if(scan)e.Result=Discovery.Run(catalogue,selected,()=>cancel,s=>worker.ReportProgress(0,s),u=>worker.ReportProgress(0,u),node);
                else e.Result=AbsReader.Run(true,s=>worker.ReportProgress(0,s));
            };
            worker.ProgressChanged+=delegate(object sender,ProgressChangedEventArgs e){var u=e.UserState as DiscoveredEcu;if(u!=null)ShowFound(u);else status.Text=Convert.ToString(e.UserState);};
            worker.RunWorkerCompleted+=delegate(object sender,RunWorkerCompletedEventArgs e){
                SetBusy(false);parked.Checked=false;
                if(e.Error!=null){details.Text="Operace selhala: "+e.Error.Message;status.Text="Operace se nepodařila. Viz podrobnosti.";}
                else if(scan){var r=(DiscoveryResult)e.Result;details.Text=r.Summary(selected)+"\r\nZáznam: "+r.LogPath;status.Text=(r.Completed?"Průchod dokončen. ":"Průchod zastaven. ")+"Odpovědělo adres: "+r.Units.Count(u=>u.Responded)+". Port je zavřený.";}
                else{var r=(ReadResult)e.Result;details.Text=r.Report+"\r\nZáznam: "+r.LogPath;status.Text=r.Success?"ABS přečteno. Port je zavřený.":"Čtení nebo ukončení nepotvrzeno — viz podrobnosti.";}
                worker.Dispose();
            };
            worker.RunWorkerAsync();
        }
        void LoadLast()
        {
            if(Vehicle==null)return;
            try{
                string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs");
                var serializer=new System.Web.Script.Serialization.JavaScriptSerializer();
                var files=Directory.Exists(folder)?Directory.GetFiles(folder,"*-discovery-*.txt.json").OrderByDescending(x=>x):Enumerable.Empty<string>();
                foreach(string file in files){var r=serializer.Deserialize<DiscoveryResult>(File.ReadAllText(file,Encoding.UTF8));if(r.VehicleId!=Vehicle.Id)continue;observed.Clear();foreach(DataGridViewRow row in grid.Rows){row.Cells["state"].Value="Nezkoušeno";row.DefaultCellStyle.ForeColor=Color.Black;}foreach(var u in r.Units)ShowFound(u);UpdateModule();details.Text="ULOŽENÝ VÝSLEDEK — bez nového připojení\r\n\r\n"+r.Summary(Vehicle);status.Text="Zobrazen uložený průchod z "+r.Started;return;}
                status.Text="Pro tento profil zatím není uložený průchod.";
            }catch(Exception e){status.Text="Záznam nelze přečíst: "+e.Message;}
        }
        void LoadFaults()
        {
            if(Node==null||Vehicle==null)return;
            try{
                DiscoveredEcu unit;var match=SelectedMatch(out unit);bool isAbs=Vehicle.Id=="VCAN:all"&&Node.Code=="ABS";
                if(match==null&&!isAbs){status.Text="Nejprve načtěte průchod a vyberte rozpoznanou jednotku.";return;}
                string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs");
                var files=Directory.Exists(folder)?Directory.GetFiles(folder,isAbs?"*-abs-*.txt.report.txt":"*-module-*.txt.report.txt").OrderByDescending(x=>x):Enumerable.Empty<string>();
                foreach(var file in files){if(new FileInfo(file).Length>1024*1024)continue;string text=File.ReadAllText(file,Encoding.UTF8);if(isAbs?text.Contains("Počet záznamů DTC:"):text.StartsWith("ČTENÍ JEDNOTKY — "+match.Model+" / "+match.Variant+"\r\n")&&text.Contains("Počet záznamů závad:")){details.Text="ULOŽENÝ ODEČET — bez nového připojení\r\n\r\n"+text+"\r\nSoubor: "+file;status.Text="Zobrazen dřívější odečet. S autem nyní nekomunikuji.";return;}}
                status.Text="Pro tuto jednotku nebyl nalezen uložený odečet závad.";
            }catch(Exception e){status.Text="Odečet nelze načíst: "+e.Message;}
        }
    }
}


