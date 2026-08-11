using System.Text;

namespace FieldOps.Features.Work;

public static class SearchTextNormalization
{
    public const string PropertyName = "SearchTextNormalized";

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string compatibilityNormalized = value.Normalize(NormalizationForm.FormKC);
        StringBuilder result = new(compatibilityNormalized.Length);
        bool pendingSpace = false;
        foreach (char character in compatibilityNormalized)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }
            result.Append(character);
        }

        return result.Length == 0 ? null : result.ToString().ToUpperInvariant();
    }

    public static string PostgresGeneratedExpression(string sourceSql) =>
        $"upper(regexp_replace(normalize(btrim({sourceSql}), NFKC), '[[:space:]]+', ' ', 'g'))";
}