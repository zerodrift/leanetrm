namespace OpenEtrm.Shared.Calculation;

public sealed class CalculationException(string code,string message):Exception(message)
{
    public string Code {get;}=code;
}
