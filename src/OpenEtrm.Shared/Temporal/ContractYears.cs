namespace OpenEtrm.Shared.Temporal;

public enum ContractYearBasis { AnniversaryStep=1 }

public static class ContractYears
{
    public const string Implementation="anniversary_step_v1";
    public static int Exponent(DateOnly baseDate,DateOnly operatingDate,ContractYearBasis basis)
    {
        if(basis!=ContractYearBasis.AnniversaryStep || baseDate.Month==2 && baseDate.Day==29 || operatingDate<baseDate)
            throw new TemporalException("contract_year_basis","Supply an anniversary-step base other than February 29, on or before the operating date.");
        var years=operatingDate.Year-baseDate.Year;
        return operatingDate<baseDate.AddYears(years)?years-1:years;
    }
}
