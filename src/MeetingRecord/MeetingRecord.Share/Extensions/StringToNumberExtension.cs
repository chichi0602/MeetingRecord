namespace MeetingRecord.Share.Extensions;

public static class StringToNumberExtension
{
    private static readonly (string Operator, double Offset)[] BloodOperators =
    [
        (">", 0.1),
        ("≧", 0.1),
        ("<", -0.1),
        ("≦", -0.1)
    ];

    public static double ToBloodDouble(this string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        foreach (var (operatorValue, offset) in BloodOperators)
        {
            var operatorIndex = value.IndexOf(operatorValue, StringComparison.Ordinal);
            if (operatorIndex < 0)
            {
                continue;
            }

            var numberPart = value[(operatorIndex + operatorValue.Length)..];
            return double.TryParse(numberPart, out var result) ? result + offset : 0;
        }

        return double.TryParse(value, out var plainResult) ? plainResult : 0;
    }

    public static double ToDouble(this string value)
    {
        return double.TryParse(value, out var result) ? result : 0;
    }

    public static float ToFloat(this string value)
    {
        return float.TryParse(value, out var result) ? result : 0;
    }

    public static int ToInt(this string value)
    {
        return int.TryParse(value, out var result) ? result : 0;
    }
}
