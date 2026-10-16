using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;

// 인증을 마친 한 수신자만 초당 약 1KB로 제한해 다른 수신자의 진행과 송신 대기를 비교한다.
internal sealed class SlowPeerProxy : IAsyncDisposable
{
    private readonly TcpListener _listener=new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource _stop=new();
    private readonly Task _relay; private TcpClient _front,_back;
    private long _throttledBytes, _throttledFrames;
    public volatile bool Enabled;
    public long ThrottledBytes=>Interlocked.Read(ref _throttledBytes);
    public long ThrottledFrames=>Interlocked.Read(ref _throttledFrames);
    public int Port=>((IPEndPoint)_listener.LocalEndpoint).Port;
    public SlowPeerProxy() { _listener.Start(); _relay=Run(); }
    private async Task Run()
    {
        try
        {
            _front=await _listener.AcceptTcpClientAsync(_stop.Token); _back=new TcpClient(); _back.ReceiveBufferSize=1024;
            await _back.ConnectAsync(IPAddress.Loopback,7777,_stop.Token); _front.NoDelay=_back.NoDelay=true;
            async Task Download()
            {
                var source=_back.GetStream(); var target=_front.GetStream();
                while(!_stop.IsCancellationRequested)
                {
                    byte[] header=new byte[4]; await source.ReadExactlyAsync(header,_stop.Token);
                    int size=BinaryPrimitives.ReadUInt16LittleEndian(header); if(size<4 || size>4096) throw new InvalidDataException("Invalid slow proxy frame");
                    byte[] body=new byte[size-4]; await source.ReadExactlyAsync(body,_stop.Token);
                    bool throttled=Enabled;
                    if(throttled) await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.02,size/1024.0)),_stop.Token);
                    await target.WriteAsync(header,_stop.Token); await target.WriteAsync(body,_stop.Token);
                    if(throttled) { Interlocked.Add(ref _throttledBytes,size); Interlocked.Increment(ref _throttledFrames); }
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
