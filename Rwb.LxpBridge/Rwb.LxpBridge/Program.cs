﻿using System.Net;
using System.Net.Sockets;
using System.Text;

// Helped a little from am exceptionally shit description at https://github.com/celsworth/lxp-bridge/wiki/TCP-Packet-Spec
// FFS the guy is a fucking moron.

// Maybe some clues at https://github.com/celsworth/lxp-packet/tree/master/lib/lxp/packet

// The guy is a fucking nightmare. https://github.com/celsworth/lxp-bridge/discussions/193

// https://github.com/zonyl/luxpower-ha/issues/2
// https://github.com/jaredmauch/eg4-bridge/issues/8

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
                            while (from < received)
                            {
                                from = ProcessNextMessage(from, buffer);
                            }
                            if (from > received)
                            {
                                throw new IndexOutOfRangeException();
                            }
                            for (int i = 0; i < received; i++)
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
                    Console.WriteLine("Heartbeat");
                    break;
                case TcpFunction.TraslatedData:
                    ProcessTranslatedData(from, buffer, h);
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
                return $"{Prefix:X} / PV: {ProtocolVersion} / L: {PacketLength} / A: {Address} / TCPF: {TcpFunction} / SN: {DatalogSerialNumber}";
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

            int r = GetShort(buffer, from + 32);

            for (int i = 18; i < received; i++)
            {
                Console.Write($"{i,5:#0} {i - 6,5:#0} {buffer[from + i],5:X}");
                if (i == 32 || (i >= 35 && i < received - 1 && i % 2 == 1))
                {
                    Console.Write($" {GetShort(buffer, from + i),5}");
                }
                else
                {
                    Console.Write($" {"",5}");
                }

                int register = -1;
                if (i >= 35 && i % 2 == 1)
                //if( i >= 34 && i % 2 == 0)
                {
                    //register = r + (i - 34) / 2;
                    register = r + (i - 35) / 2;
                    Console.Write($" d{register} ");
                }

                if (i == 18)
                {
                    Console.Write(" length");
                }
                else if (i == 20)
                {
                    Console.Write(" ?");
                }
                else if (i == 21)
                {
                    Console.Write(" device function (3: R-H, 4: R-I, 6: W-1, 10: W-+.");
                }
                else if (i == 22)
                {
                    Console.Write(" " + ASCIIEncoding.Default.GetString(buffer, from + 22, 10));
                }
                else if (i == 32)
                {
                    Console.Write(" start register ~~data length~~"); // NO, it's the register index start: 0, 40, 80, 120.
                }
                else if (i == 34)
                {
                    Console.Write(" ?"); 
                }

                if (register >= 0 && Registers.Key.ContainsKey(register))
                {
                    Console.Write(" " + Registers.Key[register]);
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
                Console.WriteLine($"{i,3:#0} ({(i - 20) / 2,3:#0}): {GetShort(buffer, i)} {(Registers.Key.ContainsKey(r) ? Registers.Key[r] : "?")}");
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

        private static class Foo
        {
            private static byte[] RequestReadInputs = new byte[]
            {
0xA1, //  0: Prefix
0x1A, //  1: Prefix
0x02, //  2: Protocol version
0x00, //  3: Protocol version
0x6F, //  4: Message length (low byte) 111
0x00, //  5: Message length (high byte)
0x01, //  6: Address -- always 1
0xC2, //  7: TCP function
0x42, //  8: SN
0x41, //  9: SN
0x31, // 10: SN
0x32, // 11: SN
0x32, // 12: SN
0x35, // 13: SN
0x30, // 14: SN
0x33, // 15: SN
0x37, // 16: SN
0x30, // 17: SN
0x00, // 18: Length
0x00, // 19: Length
0x00, // 20: ?
0x04, // 21: device function. 4 is 'read inputs'.












            };
        }
    }
}
