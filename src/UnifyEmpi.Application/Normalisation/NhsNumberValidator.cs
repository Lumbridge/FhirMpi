namespace UnifyEmpi.Application.Normalisation;

public static class NhsNumberValidator
{
    public const string IdentifierSystem = "https://fhir.nhs.uk/Id/nhs-number";
    public const string TracedTag = "traced";
    public const string GoldTag = "gold";

    public static string Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsDigit).ToArray());

    public static bool IsValid(string? value)
    {
        var normalised = Normalise(value);
        if (normalised.Length != 10)
        {
            return false;
        }

        var total = 0;
        for (var index = 0; index < 9; index++)
        {
            total += (normalised[index] - '0') * (10 - index);
        }

        var checkDigit = 11 - total % 11;
        if (checkDigit == 11)
        {
            checkDigit = 0;
        }

        return checkDigit != 10 && checkDigit == normalised[9] - '0';
    }
}
