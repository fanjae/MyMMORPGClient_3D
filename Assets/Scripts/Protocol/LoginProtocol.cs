using System.IO;

public enum LoginResult : byte
{
    Success = 0,
    InvalidCredential = 1,
    ServerError = 2,
    ProtocolMismatch = 3
}

public enum RegistrationResult : byte
{
    Success=0, DuplicateId=1, InvalidId=2, InvalidPassword=3, ServerError=4, ProtocolMismatch=5, Busy=6
}

public enum CharacterListResult : byte
{
    Success = 0,
    ServerError = 1
}

public enum CharacterSelectResult : byte
{
    Success = 0,
    InvalidCharacter = 1,
    ServerUnavailable = 2
}

public sealed class CharacterInfo
{
    public uint CharacterId;
    public string Name;
    public ushort Level;
}

public sealed class CharacterListData
{
    public CharacterListResult Result;
    public CharacterInfo[] Characters;
}

public sealed class CharacterSelectData
{
    public CharacterSelectResult Result;
    public ulong AuthKey;
    public ushort GameServerPort;
}

public static class LoginProtocol
{
    public const int MaxLoginIdLength = 32;
    public const int MaxPasswordLength = 64;
    public const int MaxCharacterNameLength = 65;
    public const uint ProtocolVersion = 2;
    public const int MaxCharacterCount = 3;

    public static string RegistrationError(string id,string password)
    {
        if(string.IsNullOrEmpty(id) || id.Length<3 || id.Length>=MaxLoginIdLength) return "ID: use 3-31 letters, numbers or underscores.";
        foreach(char value in id)
            if(!(value>='a' && value<='z') && !(value>='A' && value<='Z') && !(value>='0' && value<='9') && value!='_') return "ID: use 3-31 letters, numbers or underscores.";
        if(password==null) return "Password: use at least 8 characters, up to 63 UTF-8 bytes.";
        int count=0;
        for(int index=0;index<password.Length;++index)
        {
            char value=password[index];
            if(char.IsControl(value)) return "Password cannot contain control characters.";
            if(char.IsHighSurrogate(value)) { if(index+1>=password.Length || !char.IsLowSurrogate(password[++index])) return "Password contains invalid text."; }
            else if(char.IsLowSurrogate(value)) return "Password contains invalid text.";
            ++count;
        }
        if(count<8 || System.Text.Encoding.UTF8.GetByteCount(password)>=MaxPasswordLength) return "Password: use at least 8 characters, up to 63 UTF-8 bytes.";
        return null;
    }

    public static byte[] CreateRegistrationRequest(string id,string password)
    {
        string error=RegistrationError(id,password);
        if(error!=null) throw new System.ArgumentException(error);
        return CreateLoginRequest(id.ToLowerInvariant(),password);
    }

    public static RegistrationResult ReadRegistrationResponse(byte[] payload)
    {
        var reader=new PacketReader(payload,1); var result=(RegistrationResult)reader.ReadByte();
        if(result>RegistrationResult.Busy) throw new InvalidDataException("Invalid registration result.");
        return result;
    }

    public static string RegistrationMessage(RegistrationResult result)=>result switch
    {
        RegistrationResult.Success=>"Account and Level 1 character created. Signing in...",
        RegistrationResult.DuplicateId=>"This ID already exists. Choose another ID or LOGIN.",
        RegistrationResult.InvalidId=>"ID: use 3-31 letters, numbers or underscores.",
        RegistrationResult.InvalidPassword=>"Password: use at least 8 characters, up to 63 UTF-8 bytes; no control characters.",
        RegistrationResult.Busy=>"Registration busy. Please wait and try again.",
        RegistrationResult.ProtocolMismatch=>"Update the login server and client together.",
        _=>"Registration result unavailable. Try LOGIN first; the account may already have been saved."
    };

    public static byte[] CreateLoginRequest(string loginId, string password)
    {
        using PacketWriter writer = new();
        writer.WriteFixedString(loginId, MaxLoginIdLength);
        writer.WriteFixedString(password, MaxPasswordLength);
        writer.Write(ProtocolVersion);
        return writer.ToArray();
    }

    public static LoginResult ReadLoginResponse(byte[] payload)
    {
        PacketReader reader = new(payload, 1);
        return (LoginResult)reader.ReadByte();
    }

    public static CharacterListData ReadCharacterListResponse(byte[] payload)
    {
        PacketReader reader = new(payload, 2 + MaxCharacterCount * (4 + MaxCharacterNameLength + 2));

        CharacterListResult result = (CharacterListResult)reader.ReadByte();
        byte count = reader.ReadByte();

        if (count > MaxCharacterCount)
            throw new InvalidDataException($"Invalid character count: {count}");

        CharacterInfo[] characters = new CharacterInfo[count];

        // 서버 패킷은 최대 슬롯 수만큼 고정 길이 데이터를 보내므로 모든 슬롯을 읽어 패킷 위치 유지
        for (int i = 0; i < MaxCharacterCount; ++i)
        {
            uint characterId = reader.ReadUInt32();
            string name = reader.ReadFixedString(MaxCharacterNameLength);
            ushort level = reader.ReadUInt16();

            if (i < count)
            {
                characters[i] = new CharacterInfo
                {
                    CharacterId = characterId,
                    Name = name,
                    Level = level
                };
            }
        }

        return new CharacterListData
        {
            Result = result,
            Characters = characters
        };
    }

    public static byte[] CreateCharacterSelectRequest(uint characterId)
    {
        using PacketWriter writer = new();
        writer.Write(characterId);
        return writer.ToArray();
    }

    public static CharacterSelectData ReadCharacterSelectResponse(byte[] payload)
    {
        PacketReader reader = new(payload, 1 + 8 + 2);

        return new CharacterSelectData
        {
            Result = (CharacterSelectResult)reader.ReadByte(),
            AuthKey = reader.ReadUInt64(),
            GameServerPort = reader.ReadUInt16()
        };
    }
}
