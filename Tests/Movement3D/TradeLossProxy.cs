using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

// 확정 요청은 그대로 보내고 커밋 뒤의 거래 결과·인벤토리 응답만 선택적으로 버린다.
internal sealed class TradeLossProxy : IAsyncDisposable
{
    private readonly TcpListener _listener=new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource _stop=new();
    private readonly Task _relay;
    private TcpClient _front,_back;
    public volatile bool DropFinal;
    public int DroppedSnapshots;
    public readonly TaskCompletionSource<bool> TerminalDropped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Port=>((IPEndPoint)_listener.LocalEndpoint).Port;
    public TradeLossProxy() { _listener.Start(); _relay=Run(); }
    private async Task Run()
    {
        try
        {
            _front=await _listener.AcceptTcpClientAsync(_stop.Token); _back=new TcpClient();
            await _back.ConnectAsync(IPAddress.Loopback,7777,_stop.Token); _front.NoDelay=_back.NoDelay=true;
            async Task Download()
            {
                var source=_back.GetStream(); var target=_front.GetStream();
                while(!_stop.IsCancellationRequested)
                {
                    byte[] header=new byte[4]; await source.ReadExactlyAsync(header,_stop.Token);
                    int size=BinaryPrimitives.ReadUInt16LittleEndian(header),opcode=BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
                    if(size<4 || size>TcpSession.MaxPacketSize) throw new InvalidDataException("Invalid trade proxy frame");
                    byte[] body=new byte[size-4]; await source.ReadExactlyAsync(body,_stop.Token);
                    if(DropFinal && opcode==(ushort)GamePacketOpcode.InventorySnapshot && body.Length==1742 && BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(body.Length-8))==0)
                    { Interlocked.Increment(ref DroppedSnapshots); continue; }
                    if(DropFinal && opcode==(ushort)GamePacketOpcode.TradeState3D && body.Length==TradeProtocol.StateSize && body[16]==(byte)TradeStage.Completed)
                    { TerminalDropped.TrySetResult(true); continue; }
                    await target.WriteAsync(header,_stop.Token); await target.WriteAsync(body,_stop.Token);
                }
            }
            var upload=_front.GetStream().CopyToAsync(_back.GetStream(),_stop.Token); var download=Download();
            await Task.WhenAny(upload,download); _stop.Cancel(); _front.Close(); _back.Close();
            try { await Task.WhenAll(upload,download); } catch(Exception) {}
        }
        catch(Exception) when(_stop.IsCancellationRequested) {}
        catch(IOException) {}
        catch(SocketException) {}
    }
    public async ValueTask DisposeAsync()
    { _stop.Cancel(); _listener.Stop(); _front?.Close(); _back?.Close(); try { await _relay; } catch(OperationCanceledException) {} _stop.Dispose(); }
}
