using System;
public static class VitalsFill
{
    // 초기 조회 전 최대 수치가 0인 경우에도 잘못된 비율을 만들지 않는다.
    public static float Ratio(ushort current, ushort maximum) => maximum == 0 ? 0 : Math.Clamp((float)current / maximum, 0, 1);
}
