using System.Buffers.Binary;
using System.Text;

internal static class RegistrationTests
{
    internal static void Run()
    {
        Program.Check(LoginProtocol.RegistrationError("new_player","test1234")==null,"Valid credentials rejected");
        Program.Check(LoginProtocol.RegistrationError(new string('a',31),new string('x',63))==null,"Maximum credentials rejected");
        foreach(string id in new[]{"","ab",new string('a',32),"a b","한글계정","a\0b","x';--"})
            Program.Check(LoginProtocol.RegistrationError(id,"test1234")!=null,"Invalid ID accepted");
        foreach(string password in new[]{"","1234567",new string('x',64),"1234567\n","1234567\0","1234567\ud800"})
            Program.Check(LoginProtocol.RegistrationError("new_player",password)!=null,"Invalid password accepted");
        string unicode=string.Concat(Enumerable.Repeat("가😀",4));
        Program.Check(LoginProtocol.RegistrationError("new_player",unicode)==null,"Eight Unicode scalars rejected");
        Program.Check(LoginProtocol.RegistrationError("new_player",string.Concat(Enumerable.Repeat("😀",16)))!=null,"64-byte password accepted");
        byte[] payload=LoginProtocol.CreateRegistrationRequest("New_Player"," test1234 ");
        Program.Check(payload.Length==100 && Encoding.UTF8.GetString(payload,0,10)=="new_player" && payload[32]==32 && BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(96))==2,"Signup normalized the password or changed the wire layout");
        for(byte i=0;i<=6;++i) Program.Check((byte)LoginProtocol.ReadRegistrationResponse(new[]{i})==i,"Signup result mismatch");
        foreach(byte[] malformed in new[]{Array.Empty<byte>(),new byte[]{0,0},new byte[]{7}})
        {
            bool rejected=false; try { LoginProtocol.ReadRegistrationResponse(malformed); } catch(InvalidDataException) { rejected=true; }
            Program.Check(rejected,"Malformed signup response accepted");
        }
        Console.WriteLine("[PASS] Registration validates ID/UTF-8/password boundaries and preserves additive login wire compatibility");
    }
}
