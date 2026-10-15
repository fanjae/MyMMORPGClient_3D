internal static class WindowButtonTests
{
    public static void Run()
    {
        var gate=new WindowButtonGate3D<int>(); int presses=0;
        if(gate.Press(1,true,true)) ++presses;
        for(int repeat=0;repeat<30;++repeat)
        {
            if(gate.Press(1,false,true)) ++presses;
            if(gate.Press(1,true,true)) ++presses;
        }
        Program.Check(presses==1,"Key-code and character repeats became fresh attacks");
        gate.Remove(1); if(gate.Press(1,true,true)) ++presses;
        Program.Check(presses==2,"Release did not permit a fresh press");
        gate.Clear(); Program.Check(!gate.Press(1,false,true),"Held device was treated as fresh character input after focus reset");
        Program.Check(gate.Press(1,false,false),"Character-only accessibility input was blocked");
        Console.WriteLine("[PASS] Mixed key/character repeats execute once and release permits a fresh press");
    }
}
