using System.Text;

namespace OpenMpi.Application.Normalisation;

public static class PhoneticEncoder
{
    public static string Encode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var letters = value.Where(char.IsLetter).Select(char.ToUpperInvariant).ToArray();
        if (letters.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(8);
        var index = 0;

        if (letters.Length > 1 &&
            ((letters[0] == 'K' && letters[1] == 'N') ||
             (letters[0] == 'G' && letters[1] == 'N') ||
             (letters[0] == 'P' && letters[1] == 'N') ||
             (letters[0] == 'A' && letters[1] == 'E') ||
             (letters[0] == 'W' && letters[1] == 'R')))
        {
            index = 1;
        }

        while (index < letters.Length && builder.Length < 8)
        {
            var current = letters[index];
            var previous = index > 0 ? letters[index - 1] : '\0';
            var next = index + 1 < letters.Length ? letters[index + 1] : '\0';

            var code = current switch
            {
                'B' => previous == 'M' && index == letters.Length - 1 ? "" : "B",
                'C' when next == 'H' => "X",
                'C' when next is 'I' or 'E' or 'Y' => "S",
                'C' => "K",
                'D' when next == 'G' && index + 2 < letters.Length &&
                              letters[index + 2] is 'E' or 'I' or 'Y' => "J",
                'D' => "T",
                'F' or 'J' or 'L' or 'M' or 'N' or 'R' => current.ToString(),
                'G' when next == 'H' => "",
                'G' when next is 'E' or 'I' or 'Y' => "J",
                'G' => "K",
                'H' when IsVowel(previous) && IsVowel(next) => "H",
                'K' when previous == 'C' => "",
                'K' => "K",
                'P' when next == 'H' => "F",
                'P' => "P",
                'Q' => "K",
                'S' when next == 'H' => "X",
                'S' => "S",
                'T' when next == 'H' => "0",
                'T' when next == 'I' && index + 2 < letters.Length &&
                              letters[index + 2] is 'A' or 'O' => "X",
                'T' => "T",
                'V' => "F",
                'W' or 'Y' when IsVowel(next) => current.ToString(),
                'X' => "KS",
                'Z' => "S",
                _ when index == 0 && IsVowel(current) => current.ToString(),
                _ => ""
            };

            if (code.Length > 0 &&
                (builder.Length == 0 || !builder.ToString().EndsWith(code, StringComparison.Ordinal)))
            {
                builder.Append(code);
            }

            if ((current == 'C' || current == 'P' || current == 'S' || current == 'T') && next == 'H')
            {
                index++;
            }

            index++;
        }

        return builder.ToString();
    }

    private static bool IsVowel(char value) => value is 'A' or 'E' or 'I' or 'O' or 'U';
}
