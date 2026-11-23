/*
 * Adapted from PSA seed/key algorithm, Copyright 2020 Ludwig V.
 * Original algorithm by Wouter Bokslag and Jason F. (prototux).
 * GNU GPL v3 or later; license: data/upstream/psa-coding-reference/seedkey-LICENSE.
 * Source revision 5aef5117ac385608134fdc631e3ea5787600458c.
 * This adapter supports ONLY the identified JCAE BSI Evolue configuration level.
 * 21 FE payload byte 10 = 01 identifies Evolue; ECU_KEYS lists B001 for BSI_EV.
 * B0B0 belongs to the old JCAE BSI and must NOT be tried for this software.
 */
using System;
using System.IO;
using System.Linq;
namespace PsaReader
{
    internal static class BsiConfigurationKey
    {
        static int Transform(int data,int divisor,int subtract,int multiply)
        {
            data=unchecked((short)data);
            int result=(data%divisor)*multiply-(data/divisor)*subtract;
            return result<0?result+divisor*multiply+subtract:result;
        }
        internal static int[] FromSeed(int[] seed)
        {
            if(seed==null||seed.Length!=4||seed.Any(x=>x<0||x>255))throw new IOException("Neplatná výzva BSI.");
            int high=Transform(0xB001,0xB2,0x3F,0xAA)|Transform(seed[0]*256+seed[3],0xB1,2,0xAB);
            int low=Transform(seed[1]*256+seed[2],0xB2,0x3F,0xAA)|Transform(high,0xB1,2,0xAB);
            return new[]{(high>>8)&255,high&255,(low>>8)&255,low&255};
        }
        internal static string Command(int[] seed){return "*608_27_84_"+String.Join("_",FromSeed(seed).Select(x=>x.ToString("X2")));}
    }
}
