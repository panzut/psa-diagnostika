using System;
using System.Collections.Generic;
using System.IO;

namespace PsaReader
{
    internal static class VciTests
    {
        static int checks;
        static void Check(bool valid, string label) { if (!valid) throw new Exception("FAIL: " + label); checks++; }
        static void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); } catch (IOException) { rejected = true; }
            Check(rejected, label);
        }
        sealed class Fake : IVci
        {
            internal readonly List<VciCommand> Sent = new List<VciCommand>();
            internal int FailAt = -1;
            internal string BadReply = "*unexpected";
            int statuses;
            public string Exchange(VciCommand c)
            {
                Sent.Add(c);
                if (Sent.Count == FailAt) return BadReply;
                switch (c)
                {
                    case VciCommand.Firmware: return "*1423";
                    case VciCommand.Status: return (++statuses == 2 ? "*1 1459" : "*0 1458");
                    case VciCommand.Model: return "*CDP+";
                    case VciCommand.Serial: return "*123456";
                    case VciCommand.Pins: return "*114";
                    case VciCommand.Relays: return "*106";
                    case VciCommand.ConfigureAbs: case VciCommand.ReceiveMode: return "*256";
                    case VciCommand.Timing: return "*0 10";
                    case VciCommand.StartAbs: return "*193 208 143";
                    case VciCommand.ReadAbsDtc: return "*87 1 83 1 19";
                    case VciCommand.Disconnect: statuses = 2; return "*1";
                    default: throw new Exception("Unknown test command");
                }
            }
            public void Dispose() { }
        }
        internal static int Run()
        {
            Check(VciProtocol.DecodeAbs("*87 1 83 1 19").Contains("5301 · stav 0x13"), "captured ABS DTC");
            Check(VciProtocol.DecodeAbs("*87 0").Contains("neobsahuje DTC"), "zero DTC");
            Check(VciProtocol.DecodeAbs("*87 2 83 1 19 18 52 0").Contains("1234"), "multiple records");
            Check(VciProtocol.DecodeAbs("*87 1 83 1 0").Contains("není ověřen"), "unknown status is not labelled intermittent");
            Check(VciProtocol.DecodeAbs("*87 1 18 52 0").Contains("není v našem"), "unknown DTC is not invented");
            foreach (string invalid in new[] { "", "87 0", "*87", "*87 1", "*87 0 1", "*87 1 83 1", "*87 1 83 1 19 0", "*127 23 17", "*87 1 256 1 19", "*87 -1", "*87 1 83 1 nope" })
                Reject(() => VciProtocol.DecodeAbs(invalid), "invalid payload: " + invalid);
            foreach (string invalid in new[] { "*1 1458", "*0", "*0 1 2", "*0 999999" })
                Reject(() => VciProtocol.Status(invalid, 0), "invalid idle status");
            bool unknownRejected = false;
            try { VciProtocol.Encode((VciCommand)999); } catch (InvalidOperationException) { unknownRejected = true; }
            Check(unknownRejected, "unknown command cannot be encoded");
            var frame = new CrFrame(); string decoded = null;
            foreach (char c in "*87 1 83 1 19 \r") decoded = frame.Feed(c);
            Check(decoded == "*87 1 83 1 19", "byte-fragmented CR response");
            foreach (char c in "*1\r") decoded = frame.Feed(c);
            Check(decoded == "*1", "next frame");
            Reject(() => new CrFrame().Feed(0), "binary data rejected");
            Reject(() => new CrFrame().Feed(13), "empty frame rejected");
            Reject(() => { var f = new CrFrame(); for (int i = 0; i < 4097; i++) f.Feed(65); }, "bounded frame");
            var wire = new Fake(); var result = AbsReader.Session(wire, true, delegate { });
            Check(result.Success && result.Report.Contains("5301") && result.Report.Contains("Ukončení spojení potvrzeno"), "full captured session");
            Check(wire.Sent.Count == 15 && wire.Sent[12] == VciCommand.ReadAbsDtc && wire.Sent[13] == VciCommand.Disconnect && wire.Sent[14] == VciCommand.Status, "sequence terminates once");
            var identity = new Fake();
            Check(AbsReader.Session(identity, false, delegate { }).Success && identity.Sent.Count == 5, "identity does not initialize vehicle");
            for (int failure = 1; failure <= 15; failure++)
            {
                var bad = new Fake { FailAt = failure };
                var failed = AbsReader.Session(bad, true, delegate { });
                Check(!failed.Success, "failure propagates at step " + failure);
                if (failure <= 5) Check(bad.Sent.Count == failure, "identity mismatch stops before bus at " + failure);
                else if (failure <= 13)
                {
                    Check(bad.Sent[failure] == VciCommand.Disconnect, "failure goes straight to teardown at " + failure);
                    Check(bad.Sent.Count == failure + 2, "no retry or other work after failure " + failure);
                }
                else Check(failed.Report.Contains("UKONČENÍ NEPOTVRZENO"), "teardown failure visible");
            }
            var negative = new Fake { FailAt = 13, BadReply = "*127 23 17" };
            Check(!AbsReader.Session(negative, true, delegate { }).Success && negative.Sent[13] == VciCommand.Disconnect, "negative ECU reply still disconnects");
            Console.WriteLine("PASS: " + checks + " VCI/profile checks; simulated transport, no device IO.");
            return 0;
        }
    }
}
