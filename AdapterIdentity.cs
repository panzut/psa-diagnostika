using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace PsaReader
{
    internal static class AdapterIdentity
    {
        // Version/serial numbers identify the adapter; they do not grant ECU write
        // capabilities. Every adapter command still requires its exact expected ACK.
        internal static string Number(string response,string label)
        {
            int[] n=VciProtocol.Numbers(response);
            if(n.Length!=1||n[0]<=0)throw new IOException("Neplatné "+label+" adaptéru: "+response);
            return n[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        internal static string SelectPort(IEnumerable<string> candidates,IEnumerable<string> present)
        {
            var ports=candidates.Where(p=>!String.IsNullOrEmpty(p)&&present.Contains(p,StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if(ports.Length==0)throw new IOException("USB DS150E/CDP+ nebyl nalezen. Připojte adaptér a ověřte jeho ovladač ve Windows.");
            if(ports.Length!=1)throw new IOException("Je připojeno více adaptérů DS150E/CDP+: "+String.Join(", ",ports)+". Ponechte připojený jeden.");
            return ports[0];
        }
        internal static string FindPort()
        {
            var ports=new List<string>();
            using(var root=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\FTDIBUS")){
                if(root!=null)foreach(string device in root.GetSubKeyNames().Where(n=>n.StartsWith("VID_0403+PID_D6DA+",StringComparison.OrdinalIgnoreCase))){
                    using(var item=root.OpenSubKey(device))foreach(string instance in item.GetSubKeyNames())using(var parameters=item.OpenSubKey(instance+@"\Device Parameters")){
                        string port=parameters==null?null:parameters.GetValue("PortName") as string;if(port!=null)ports.Add(port);
                    }
                }
            }
            return SelectPort(ports,System.IO.Ports.SerialPort.GetPortNames());
        }
    }
}
