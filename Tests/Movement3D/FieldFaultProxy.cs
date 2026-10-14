using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;

// 실제 서버의 커밋 뒤 게임 응답만 유실시키며 요청을 복제하거나 아이템을 직접 지급하지 않는다.
internal static class FieldFaultProxy
{
    private static int _disconnectedPickup;
    internal static async Task<int> RunAsync(int port,string control)
    {
        var listener=new TcpListener(IPAddress.Loopback,port); listener.Start();
        Console.WriteLine($"READY fault proxy {port} control={control}");
        while (true)
        {
            var client=await listener.AcceptTcpClientAsync();
            _=Relay(client,control);
        }
    }
    private static async Task Relay(TcpClient client,string control)
    {
        using (client) using (var server=new TcpClient()) using (var canceled=new CancellationTokenSource())
        {
            try
            {
                await server.ConnectAsync(IPAddress.Loopback,7777);
                var potions=new System.Collections.Concurrent.ConcurrentDictionary<ulong,bool>(); bool pickup=false,vitals=false;
                async Task Pump(NetworkStream source,NetworkStream target,bool inbound)
                {
                    while (!canceled.IsCancellationRequested)
                    {
                        byte[] header=new byte[4]; await source.ReadExactlyAsync(header,canceled.Token);
                        ushort size=BinaryPrimitives.ReadUInt16LittleEndian(header), opcode=BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
                        if (size<4 || size>4096) throw new InvalidDataException("Invalid proxy frame");
                        byte[] body=new byte[size-4]; await source.ReadExactlyAsync(body,canceled.Token);
                        string mode=File.Exists(control) ? File.ReadAllText(control).Trim() : "none";
                        bool drop=false;
                        if (inbound)
                        {
                            if (opcode==(ushort)GamePacketOpcode.InventoryRequest && body.Length==20 && body[0]==(byte)InventoryOperation.Use)
                                potions[BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(12))]=true;
                            if (opcode==(ushort)GamePacketOpcode.PickupRequest3D || opcode==(ushort)GamePacketOpcode.PickupStatusRequest3D) pickup=true;
                            if (opcode==(ushort)GamePacketOpcode.PickupStatusRequest3D) Console.WriteLine("QUERY pickup result");
                        }
                        else
                        {
                            if (mode=="potion" && opcode==(ushort)GamePacketOpcode.InventorySnapshot && body.Length==1742 &&
                                potions.ContainsKey(BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(body.Length-8)))) drop=true;
                            if (pickup && mode=="pickup" && (opcode==(ushort)GamePacketOpcode.PickupResponse3D ||
                                (opcode==(ushort)GamePacketOpcode.InventorySnapshot && body.Length==1742 && BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(body.Length-8))==0))) drop=true;
                            if (mode=="vitals" && !vitals && opcode==(ushort)GamePacketOpcode.Vitals3D) { vitals=true; drop=true; }
                            if (pickup && mode=="pickup-disconnect" && opcode==(ushort)GamePacketOpcode.PickupResponse3D && Interlocked.CompareExchange(ref _disconnectedPickup,1,0)==0)
                            { Console.WriteLine("DROP committed pickup response and disconnect"); canceled.Cancel(); client.Close(); server.Close(); return; }
                        }
                        if (drop) { Console.WriteLine($"DROP opcode={opcode} bytes={size} mode={mode}"); continue; }
                        await target.WriteAsync(header,canceled.Token); await target.WriteAsync(body,canceled.Token);
                    }
                }
                var upload=Pump(client.GetStream(),server.GetStream(),true); var download=Pump(server.GetStream(),client.GetStream(),false);
                await Task.WhenAny(upload,download); canceled.Cancel(); client.Close(); server.Close();
                try { await Task.WhenAll(upload,download); } catch (Exception) { }
            }
            catch (Exception error) { Console.WriteLine("Proxy connection ended: "+error.GetType().Name); }
        }
    }
}
