using System.Net;
using System.Net.Sockets;
using System.Text;

// Helped a little from am exceptionally shit description at https://github.com/celsworth/lxp-bridge/wiki/TCP-Packet-Spec
// FFS the guy is a fucking moron.

// Maybe some clues at https://github.com/celsworth/lxp-packet/tree/master/lib/lxp/packet

/* It would be nice to do something in (POSIX) sh.
nc 192.168.0.60 8000 | stdbuf -i0 -o0 hexdump -v -e '1/1 "%.2x\n"' | while read line;
do
        echo "line $line"
done
*/

namespace Rwb.LxpBridge
{
    internal class Program
    {
        static void Main(string[] args)
        {
            IPEndPoint ipEndPoint = new IPEndPoint(IPAddress.Parse("192.168.0.60"), 8000);
            while (true)
            {
                try
                {
                    using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                    {
                        byte[] buffer = new byte[1_024];
                        s.Connect(ipEndPoint);
                        while (true)
                        {
                            int received = s.Receive(buffer, SocketFlags.None);
                            // What was received may be more than one LUX message.
                            int from = 0;
                            while(from < received)
                            {
                                from = ProcessNextMessage(from, buffer);
                            }
                            if(from > received)
                            {
                                throw new IndexOutOfRangeException();
                            }
                            for(int i=0; i < received; i++)
                            {
                                buffer[i] = 0;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Failed: " + e.Message);
                    Console.WriteLine("Reconnecting...");
                }
            }
        }

        private static int ProcessNextMessage(int from, byte[] buffer)
        {
            Header h = ParseHeader(from, buffer);
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} Received: {h}.");
            PrintBuffer(from, buffer, h, h.PacketLength + 6);
            switch (h.TcpFunction)
            {
                case TcpFunction.Heartbeat:
                    break;
                case TcpFunction.TraslatedData:
                    //ProcessTranslatedData(from, buffer, h);
                    break;
                default:
                    throw new NotImplementedException();
            }
            return from + 6 + h.PacketLength;
        }

        class Header
        {
            public ushort Prefix { get; set; }
            public ushort ProtocolVersion { get; set; }

            /// <summary>
            /// This excludes the first 6 bytes (prefix, protocol, length) of the total message.
            /// </summary>
            public ushort PacketLength { get; set; }
            public byte Address { get; set; }
            public TcpFunction TcpFunction { get; set; }
            public string DatalogSerialNumber { get; set; } // 'dongle' serial number BA12250370

            public override string ToString()
            {
                return $"{Prefix:X} / {ProtocolVersion} / L: {PacketLength} / {Address} / {TcpFunction} / {DatalogSerialNumber} ";
            }
        }

        public enum TcpFunction
        {
            /// <summary>
            /// 193
            /// </summary>
            Heartbeat = 0xc1,

            /// <summary>
            /// 194
            /// </summary>
            TraslatedData = 0xc2,

            /// <summary>
            /// 195
            /// </summary>
            ReadParam = 0xc3,

            /// <summary>
            /// 196
            /// </summary>
            WriteParam = 0xc4
        }

        private static void PrintBuffer(int from, byte[] buffer, Header h, int received)
        {
            Console.WriteLine("Payload");
            Console.WriteLine("Index Index-6 Value Int16");

            for (int i = 18; i < received; i++)
            {
                Console.Write($"{i,5:#0} {i-6,5:#0} {buffer[from + i],5:X}");
                if (i >= 18 && i < received - 1) { Console.Write($" {GetShort(buffer, from + i),5}"); }
                if( i == 18)
                {
                    Console.Write(" length");
                }
                else if(i == 20)
                {
                    Console.Write(" device function");
                }
                else if(i == 22)
                {
                    Console.Write(" " + ASCIIEncoding.Default.GetString(buffer, from + 22, 10));
                }
                else if( i == 32)
                {
                    Console.Write(" data length");
                }
                else if (i == 34)
                {
                    Console.Write(" status");
                }
                else if (i == 36)
                {
                    Console.Write(" v_pv_1");
                }
                else if (i == 38)
                {
                    Console.Write(" v_pv_2");
                }
                else if (i == 40)
                {
                    Console.Write(" v_pv_3");
                }
                else if (i == 42)
                {
                    Console.Write(" v_batt");
                }
                else if (i == 44)
                {
                    Console.Write(" soc");
                }
                Console.WriteLine();
            }
        }

        private static ushort GetShort(byte[] buffer, int offset)
        {
            return BitConverter.ToUInt16(new byte[] { buffer[offset], buffer[offset + 1] });
        }

        private static Header ParseHeader(int from, byte[] buffer)
        {
            return new Header()
            {
                Prefix = GetShort(buffer, from + 0),
                ProtocolVersion = GetShort(buffer, from + 2),
                PacketLength = GetShort(buffer, from + 4),
                Address = buffer[from + 6],
                TcpFunction = (TcpFunction)buffer[from + 7],
                DatalogSerialNumber = ASCIIEncoding.Default.GetString(buffer, from + 8, 10)
            };
        }

        private static void ProcessTranslatedData(int from, byte[] buffer, Header h)
        {
            int slap = from + 6;
            // Header again.
            byte address = buffer[from + slap]; // 0 to write, 1 to read.
            DeviceFunction function = (DeviceFunction)buffer[from + slap + 1];
            string inverterSerialNumber = ASCIIEncoding.Default.GetString(buffer, from + slap + 2, 10);
            ushort dataLength = GetShort(buffer, from + slap + 2 + 10);

            // Data.
            byte bank = 0;
            Console.WriteLine($"{(address == 1 ? "r" : "w")} {function}");
            for (int i = from + slap + 12; i < from + slap + dataLength; i += 2)
            {
                byte r = Convert.ToByte(40 * bank + i - 14);
                Console.WriteLine($"{i,3:#0} ({(i-20)/2,3:#0}): {GetShort(buffer, i)} {(Registers.Key.ContainsKey(r) ? Registers.Key[r] : "?")}");
            }
        }

        enum DeviceFunction
        {
            ReadHolding = 0x3,
            ReadInput = 0x4,
            WriteHoldingSingle = 0x6,
            WriteHoldingMulti = 0x10,
        }

        class TranslatedDatum
        {
            public byte Address { get; set; }
            public byte DeviceFunction { get; set; }
            public string InverterSerialNumber { get; set; }
            public short DataLength { get; set; }
        }
    }
}
