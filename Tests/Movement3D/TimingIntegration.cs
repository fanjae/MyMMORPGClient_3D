using System.Diagnostics;

internal static class TimingIntegration
{
    public static async Task<int> RunAsync()
    {
        string previous=Environment.GetEnvironmentVariable("MMORPG_TEST_GAME_PORT");
        try
        {
            foreach(var condition in new[]{(0,0),(80,35),(150,60)})
            {
                await using var proxy=new DelayedGameProxy(condition.Item1,condition.Item2);
                Environment.SetEnvironmentVariable("MMORPG_TEST_GAME_PORT",proxy.Port.ToString());
                using FieldServerIntegration.Peer peer=new(); await peer.ConnectAsync("test",1002);
                var timing=new NetworkTiming3D(); var playback=new AdaptivePlayback3D(); var watch=Stopwatch.StartNew();
                for(int sample=0;sample<8;++sample)
                {
                    ulong sequence;
                    while(!timing.TryBegin(watch.Elapsed.TotalSeconds,out sequence)) await Task.Delay(10);
                    using PacketWriter writer=new(); writer.Write(sequence); await peer.SendAsync(GamePacketOpcode.TimingPing3D,writer.ToArray());
                    await peer.PumpAsync(()=>peer.Pong==sequence);
                    Program.Check(timing.Accept(sequence,watch.Elapsed.TotalSeconds),"Actual RTT response identity failed");
                    playback.SetNetwork(timing.RttSeconds,timing.VariationSeconds,timing.Samples); playback.Advance(watch.Elapsed.TotalSeconds);
                }
                Program.Check(timing.RttSeconds*1000>=2*(condition.Item1-condition.Item2)-15 && timing.RttSeconds<4,"Measured RTT contradicts injected delay");
                Program.Check(playback.BufferSeconds>=0.2 && playback.BufferSeconds<=0.4,"Measured network escaped playback bounds");
                Console.WriteLine($"[PASS] Actual timing oneWay={condition.Item1}±{condition.Item2}ms samples={timing.Samples} rtt={timing.RttSeconds*1000:F2}ms variation={timing.VariationSeconds*1000:F2}ms buffer={playback.BufferSeconds*1000:F2}ms");
            }
            return 0;
        }
        finally { Environment.SetEnvironmentVariable("MMORPG_TEST_GAME_PORT",previous); }
    }
}
