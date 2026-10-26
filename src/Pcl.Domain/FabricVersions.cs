using System.Numerics;
using System.Text.RegularExpressions;

namespace Pcl.Domain;
/// <summary>Fabric range conjunctions and string-version equality, independent of JSON and IO.</summary>
public static class FabricVersions
{
    private sealed record Semantic(int[] Parts, string? Pre);
    private static Semantic? Parse(string text)
    {
        var match = Regex.Match(text, @"^(\d+(?:\.\d+)*)(?:-([0-9A-Za-z.-]*))?(?:\+[0-9A-Za-z.-]*)?$");
        if (!match.Success)
        {
            return null;
        }

        var parts = new List<int>();
        foreach (var value in match.Groups[1].Value.Split('.'))
        {
            if (!int.TryParse(value, out var number))
            {
                return null;
            }

            parts.Add(number);
        }

        return new(
            parts.ToArray(),
            match.Groups[2].Success
                ? match.Groups[2].Value
                : null
        );
    }

    public static int Compare(string left, string right)
    {
        var a = Parse(left);
        var b = Parse(right);
        if (a is null || b is null)
        {
            return string.Compare(left, right, StringComparison.Ordinal);
        }

        for (var i = 0; i < Math.Max(a.Parts.Length, b.Parts.Length); i++)
        {
            var cmp = (i < a.Parts.Length
                ? a.Parts[i]
                : 0).CompareTo(
                i < b.Parts.Length
                    ? b.Parts[i]
                    : 0
            );
            if (cmp != 0)
            {
                return cmp;
            }
        }

        if (a.Pre == b.Pre)
        {
            return 0;
        }

        if (a.Pre is null || b.Pre is null)
        {
            return a.Pre is null
                ? 1
                : -1;
        }

        var ap = a.Pre.Split('.');
        var bp = b.Pre.Split('.');
        for (var i = 0; i < Math.Min(ap.Length, bp.Length); i++)
        {
            var an = BigInteger.TryParse(ap[i], out var av);
            var bn = BigInteger.TryParse(bp[i], out var bv);
            var cmp = an && bn
                ? av.CompareTo(bv)
                : an != bn
                    ? (an
                        ? -1
                        : 1)
                    : string.Compare(ap[i], bp[i], StringComparison.Ordinal);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return ap.Length.CompareTo(bp.Length);
    }

    public static void Validate(string predicate) => _ = Evaluate("0", predicate, true);
    public static bool Matches(string version, string predicate) => Evaluate(version, predicate, false);
    private static bool Evaluate(string version, string predicate, bool validateOnly)
    {
        var matches = true;
        foreach (var raw in predicate.Split(' '))
        {
            var token = raw.Trim();
            if (token is "" or "*")
            {
                continue;
            }

            var op = new[]
            {
                ">=",
                "<=",
                ">",
                "<",
                "=",
                "~",
                "^"
            }.FirstOrDefault(
                x => token.StartsWith(x, StringComparison.Ordinal)
            ) ?? "=";
            var target = token.StartsWith(op, StringComparison.Ordinal)
                ? token[op.Length..]
                : token;
            if (target.Length == 0)
            {
                throw new InvalidDataException("Empty Fabric version predicate.");
            }

            var parts = target.Split('.');
            var wildcard = Array.FindIndex(
                parts,
                x => x is "x" or "X" or "*"
            );
            if (wildcard >= 0)
            {
                if (op != "=" || wildcard == 0 || parts.Skip(wildcard)
                    .Any(
                        x => x is not ("x" or "X" or "*")
                    ) || parts.Take(wildcard)
                    .Any(
                        x => !int.TryParse(x, out _)
                    ))
                {
                    throw new InvalidDataException("Invalid Fabric wildcard range.");
                }

                var low = parts.Take(wildcard)
                    .Select(int.Parse)
                    .ToArray();
                var high = low.ToArray();
                high[^1] = checked(high[^1] + 1);
                matches &= Parse(version)is not null && Compare(
                    version,
                    string.Join('.', low) + "-"
                ) >= 0 && Compare(
                    version,
                    string.Join('.', high) + "-"
                ) < 0;
                continue;
            }

            var semantic = Parse(target);
            if (semantic is null)
            {
                if (op is ">" or "<")
                {
                    throw new InvalidDataException("Exclusive Fabric ranges require semantic versions.");
                }

                matches &= version == target;
                continue;
            }

            var cmp = Compare(version, target);
            var upperBound = "";
            if (op is "~" or "^")
            {
                var upper = semantic.Parts.ToList();
                while (upper.Count < 2)
                {
                    upper.Add(0);
                }

                var index = op == "~"
                    ? 1
                    : 0;
                upper[index] = checked(upper[index] + 1);
                for (var i = index + 1; i < upper.Count; i++)
                {
                    upper[i] = 0;
                }

                upperBound = string.Join('.', upper) + "-";
            }

            matches &= Parse(version)is not null && (op switch
            {
                ">=" => cmp >= 0,
                "<=" => cmp <= 0,
                ">" => cmp > 0,
                "<" => cmp < 0,
                "~" or "^" => cmp >= 0 && Compare(version, upperBound) < 0,
                _ => cmp == 0
            });
        }

        return validateOnly || matches;
    }
}
