using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

// 독립 DB의 실제 LoginServer에 가입한 뒤 기본 캐릭터로 GameServer까지 입장한다.
internal static class RegistrationIntegration
{
    private const string Password="test1234";
    private sealed class Login : IDisposable
    {
        private readonly TcpSession _session=new();
        private readonly Channel<(ushort Op,byte[] Data)> _packets=Channel.CreateUnbounded<(ushort,byte[])>();
        private readonly List<(ushort Op,byte[] Data)> _pending=new();
        internal Login() { _session.PacketReceived+=(op,data)=>_packets.Writer.TryWrite((op,data)); _session.Disconnected+=()=>_packets.Writer.TryComplete(); }
        internal Task Connect(int port=7776)=>_session.ConnectAsync("127.0.0.1",port);
        internal Task Send(LoginPacketOpcode op,byte[] data)=>_session.SendAsync((ushort)op,data);
        private async Task<byte[]> Read(LoginPacketOpcode opcode)
        {
            int index=_pending.FindIndex(p=>p.Op==(ushort)opcode);
            if(index>=0) { byte[] data=_pending[index].Data; _pending.RemoveAt(index); return data; }
            using CancellationTokenSource timeout=new(10000);
            while(true) { var packet=await _packets.Reader.ReadAsync(timeout.Token); if(packet.Op==(ushort)opcode) return packet.Data; _pending.Add(packet); }
        }
        internal async Task<RegistrationResult> Register(byte[] data)
        { await Send(LoginPacketOpcode.RegistrationRequest,data); return LoginProtocol.ReadRegistrationResponse(await Read(LoginPacketOpcode.RegistrationResponse)); }
        internal Task<RegistrationResult> Register(string id,string password=Password)=>Register(LoginProtocol.CreateLoginRequest(id,password));
        internal async Task<LoginResult> Authenticate(string id,string password=Password)
        { await Send(LoginPacketOpcode.LoginRequest,LoginProtocol.CreateLoginRequest(id,password)); return LoginProtocol.ReadLoginResponse(await Read(LoginPacketOpcode.LoginResponse)); }
        internal async Task<CharacterInfo[]> Characters()
        { await Send(LoginPacketOpcode.CharacterListRequest,Array.Empty<byte>()); var list=LoginProtocol.ReadCharacterListResponse(await Read(LoginPacketOpcode.CharacterListResponse)); Program.Check(list.Result==CharacterListResult.Success,"Character list failed"); return list.Characters; }
        public void Dispose()=>_session.Dispose();
    }
    private sealed class LostSignup : IAsyncDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _stop=new();
        private readonly Task _run; private TcpClient _front,_back;
        internal readonly TaskCompletionSource<bool> Dropped=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Port=>((IPEndPoint)_listener.LocalEndpoint).Port;
        internal LostSignup() { _listener.Start(); _run=Run(); }
        private async Task Run()
        {
            try
            {
                _front=await _listener.AcceptTcpClientAsync(_stop.Token); _back=new(); await _back.ConnectAsync(IPAddress.Loopback,7776,_stop.Token);
                var upload=_front.GetStream().CopyToAsync(_back.GetStream(),_stop.Token);
                async Task Download()
                {
                    var source=_back.GetStream();
                    while(true)
                    {
                        byte[] header=new byte[4]; await source.ReadExactlyAsync(header,_stop.Token);
                        int size=BinaryPrimitives.ReadUInt16LittleEndian(header); Program.Check(size>=4 && size<=4096,"Invalid login frame");
                        byte[] body=new byte[size-4]; await source.ReadExactlyAsync(body,_stop.Token);
                        if(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2))==(ushort)LoginPacketOpcode.RegistrationResponse && body.Length==1 && body[0]==0)
                        { Dropped.TrySetResult(true); return; }
                        await _front.GetStream().WriteAsync(header,_stop.Token); await _front.GetStream().WriteAsync(body,_stop.Token);
                    }
                }
                var download=Download(); await Task.WhenAny(upload,download); _stop.Cancel(); _front.Close(); _back.Close();
                try { await Task.WhenAll(upload,download); } catch(Exception) {}
            }
            catch(Exception error) { Dropped.TrySetException(error); }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); _front?.Close(); _back?.Close(); try { await _run; } catch(Exception) {} _stop.Dispose(); }
    }
    internal static async Task<int> RunAsync(string directory)
    {
        Directory.CreateDirectory(directory); var accounts=new List<object>();
        async Task<uint> Inspect(string id,string password=Password)
        {
            using Login login=new(); await login.Connect(); Program.Check(await login.Authenticate(id,password)==LoginResult.Success,"Created account cannot login");
            var characters=await login.Characters(); Program.Check(characters.Length==1 && characters[0].Level==1 && characters[0].Name=="Player"+characters[0].CharacterId,"Default character missing, duplicated or incorrectly named");
            accounts.Add(new{id=id.ToLowerInvariant(),character=characters[0].CharacterId}); return characters[0].CharacterId;
        }
        async Task<RegistrationResult> Register(string id,string password=Password)
        { using Login login=new(); await login.Connect(); return await login.Register(id,password); }
        Program.Check(await Register("Signup_Main")==RegistrationResult.Success,"Valid signup failed");
        uint character=await Inspect("signup_main");
        using(var peer=new FieldServerIntegration.Peer()) { await peer.ConnectAsync("signup_main",character); Program.Check(peer.Connected && peer.Inventory.Entries.Any(e=>e.ItemId!=0),"Starter character cannot enter the game with inventory"); }
        using(var login=new Login())
        {
            await login.Connect(); Program.Check(await login.Authenticate("signup_main","wrongpass")==LoginResult.InvalidCredential,"Wrong password authenticated");
            Program.Check(await login.Authenticate("SIGNUP_MAIN")==LoginResult.Success,"Case-insensitive login failed");
            Program.Check(await login.Register("another_id")==RegistrationResult.Busy,"Authenticated session created another account");
        }
        Program.Check(await Register("SIGNUP_MAIN","different123")==RegistrationResult.DuplicateId,"Duplicate ID replaced password");
        using(var login=new Login()) { await login.Connect(); Program.Check(await login.Authenticate("signup_main")==LoginResult.Success,"Duplicate request changed the original password"); }
        foreach(string id in new[]{"ab","a b","한글계정","x';--","a\0b"}) Program.Check(await Register(id)==RegistrationResult.InvalidId,"Server accepted invalid ID");
        foreach(string password in new[]{"1234567","1234567\n","1234567\0"}) Program.Check(await Register("invalid_password",password)==RegistrationResult.InvalidPassword,"Server accepted invalid password");
        using(var login=new Login())
        {
            await login.Connect(); byte[] invalid=LoginProtocol.CreateLoginRequest("wrong_version",Password); BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(96),1);
            Program.Check(await login.Register(invalid)==RegistrationResult.ProtocolMismatch,"Wrong signup version accepted");
        }
        Program.Check(await Register(new string('a',31),new string('x',63))==RegistrationResult.Success,"Boundary signup failed"); await Inspect(new string('a',31),new string('x',63));
        string unicode=string.Concat(Enumerable.Repeat("가😀",4));
        Program.Check(await Register("unicode_password",unicode)==RegistrationResult.Success,"Unicode signup failed"); await Inspect("unicode_password",unicode);
        Program.Check(await Register("spaced_password"," test1234 ")==RegistrationResult.Success,"Spaced password signup failed"); await Inspect("spaced_password"," test1234 ");
        using(var legacy=new Login()) { await legacy.Connect(); Program.Check(await legacy.Authenticate("test")==LoginResult.Success,"Existing login compatibility regressed"); }
        Console.WriteLine("[PASS] Actual signup validates credentials, preserves password bytes, creates one Lv1 character and enters the game; existing login remains compatible");

        var duplicate=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Register("parallel_same")));
        Program.Check(duplicate.Count(r=>r==RegistrationResult.Success)==1 && duplicate.All(r=>r is RegistrationResult.Success or RegistrationResult.DuplicateId),"Concurrent duplicate signup did not create exactly once");
        await Inspect("parallel_same");
        var burst=await Task.WhenAll(Enumerable.Range(0,16).Select(async i=> (Id:"burst_"+i,Result:await Register("burst_"+i))));
        Program.Check(burst.Any(r=>r.Result==RegistrationResult.Success) && burst.All(r=>r.Result is RegistrationResult.Success or RegistrationResult.Busy),"Registration burst returned invalid results");
        foreach(var created in burst.Where(r=>r.Result==RegistrationResult.Success)) await Inspect(created.Id);
        using(var cooldown=new Login())
        {
            await cooldown.Connect(); Program.Check(await cooldown.Register("ab")==RegistrationResult.InvalidId,"Invalid ID did not reject");
            Program.Check(await cooldown.Register("cooldown_user")==RegistrationResult.Busy,"Per-session signup cooldown bypassed");
            await Task.Delay(2100); Program.Check(await cooldown.Register("cooldown_user")==RegistrationResult.Success,"Signup cooldown never recovered");
        }
        await Inspect("cooldown_user");
        Console.WriteLine($"[PASS] Actual concurrent duplicate signup commits once; bounded burst accepted={burst.Count(r=>r.Result==RegistrationResult.Success)} busy={burst.Count(r=>r.Result==RegistrationResult.Busy)}; cooldown recovers");

        await using(var loss=new LostSignup())
        {
            using Login login=new(); await login.Connect(loss.Port);
            var request=login.Register("lost_response"); await loss.Dropped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            bool failed=false; try { await request; } catch(Exception) { failed=true; }
            Program.Check(failed,"Dropped signup result reached client");
        }
        await Inspect("lost_response"); Program.Check(await Register("lost_response")==RegistrationResult.DuplicateId,"Lost signup response created a second account");
        Console.WriteLine("[PASS] Committed signup response loss recovers through login without another account or character");

        File.WriteAllText(Path.Combine(directory,"registration-fixture-ready"),"ready");
        using(var timeout=new CancellationTokenSource(10000)) while(!File.Exists(Path.Combine(directory,"registration-failure-ready"))) await Task.Delay(50,timeout.Token);
        Program.Check(await Register("rollback_user")==RegistrationResult.ServerError,"Character insert failure did not reject signup");
        using(var login=new Login()) { await login.Connect(); Program.Check(await login.Authenticate("rollback_user")==LoginResult.InvalidCredential,"Character failure left an orphan account"); }
        File.WriteAllText(Path.Combine(directory,"registration-failure-checked"),"checked");
        using(var timeout=new CancellationTokenSource(10000)) while(!File.Exists(Path.Combine(directory,"registration-retry-ready"))) await Task.Delay(50,timeout.Token);
        Program.Check(await Register("rollback_user")==RegistrationResult.Success,"Rolled-back ID could not be retried"); await Inspect("rollback_user");
        File.WriteAllText(Path.Combine(directory,"registration-accounts.json"),JsonSerializer.Serialize(accounts));
        Console.WriteLine("[PASS] Actual character creation failure rolls the account back; retry commits account and character together");
        return 0;
    }
}
