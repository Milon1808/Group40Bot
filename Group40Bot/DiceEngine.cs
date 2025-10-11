using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Group40Bot;

/// <summary>Result category of a roll.</summary>
public enum RollKind { Arithmetic, Warhammer }

/// <summary>Top-level roll result.</summary>
public sealed class RollResult
{
    public RollKind Kind { get; init; }
    public string Canonical { get; init; } = "";

    // Arithmetic
    public int Total { get; init; }
    public string Breakdown { get; init; } = "";

    // Warhammer
    public WarhammerResult? Warhammer { get; init; }
}

/// <summary>Warhammer d100 evaluation result.</summary>
public sealed class WarhammerResult
{
    /// <summary>Final target after inline modifier (w50+10 -> 60).</summary>
    public int Target { get; init; }
    /// <summary>One line per die result, e.g. "22 → SL +3 (CRIT SUCCESS)".</summary>
    public List<string> RollLines { get; init; } = new();
}

/// <summary>
/// Dice roller supporting:
/// - Arithmetic expressions with NdS[!!], integers, operators + - * / (no parentheses)
/// - Warhammer pattern: ^(?<n>\d*)d(?<s>\d+)(?<bang>!!)?w(?<t>\d+)(?<tmod>[+-]\d+)?$
///   * Typically d100; explosion is ignored in warhammer mode.
///   * SL = floor(T/10) - floor(R/10).
///   * **Auto-crit rules** (d100): 1–5 ⇒ critical success, 96–100 ⇒ critical failure (override result).
///   * Doubles (11,22,…,99): under target ⇒ crit success, over target ⇒ crit failure.
/// RNG uses RandomNumberGenerator for uniform cryptographic randomness.
/// </summary>
public interface IDiceRoller
{
    RollResult Evaluate(string expr);
}

public sealed class DiceRoller : IDiceRoller
{
    private static readonly Regex WarhammerRe = new(
        @"^(?<n>\d*)d(?<s>\d+)(?<bang>!!)?w(?<t>\d+)(?<tmod>(?:[+\-]\d+)?)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TokenRe = new(
        @"(?<dice>(?<cnt>\d*)d(?<sides>\d+)(?<bang>!!)?)|(?<num>\d+)|(?<op>[+\-*/])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public RollResult Evaluate(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            throw new ArgumentException("Empty expression.");

        var compact = Regex.Replace(expr, @"\s+", "");

        // Warhammer?
        var wm = WarhammerRe.Match(compact);
        if (wm.Success)
            return EvaluateWarhammer(wm, compact);

        // Arithmetic
        return EvaluateArithmetic(compact);
    }

    // -------- Warhammer (NdS!!wT[+|-M]) --------
    private static RollResult EvaluateWarhammer(Match m, string canonical)
    {
        int count = string.IsNullOrEmpty(m.Groups["n"].Value) ? 1 : int.Parse(m.Groups["n"].Value);
        int sides = int.Parse(m.Groups["s"].Value);
        int target = int.Parse(m.Groups["t"].Value);
        if (m.Groups["tmod"].Success && !string.IsNullOrEmpty(m.Groups["tmod"].Value))
            target += int.Parse(m.Groups["tmod"].Value);

        // Clamp target to sane 1..100
        target = Math.Clamp(target, 1, 100);

        var lines = new List<string>();
        for (int i = 0; i < count; i++)
        {
            int roll = RollInt(1, sides + 1);

            // Base success
            bool success = roll <= target;

            // SL: tens(target) - tens(roll); make negative on failure
            int sl = (target / 10) - (roll / 10);
            if (!success) sl = -Math.Abs(sl);

            // --- Critical rules ---
            bool critSuccess = false, critFail = false;

            // Auto-crit applies for d100 only (Warhammer)
            if (sides == 100)
            {
                if (roll >= 1 && roll <= 5) { success = true; critSuccess = true; }
                else if (roll >= 96 && roll <= 100) { success = false; critFail = true; }
            }

            // Doubles (11..99) criticals depending on success/failure (unless already auto-crit)
            if (!critSuccess && !critFail && IsDoubles(roll))
            {
                if (success) critSuccess = true; else critFail = true;
            }

            string tag = success
                ? (critSuccess ? "CRIT SUCCESS" : "Success")
                : (critFail ? "CRIT FAIL" : "Fail");

            string slText = sl >= 0 ? $"+{sl}" : sl.ToString();
            lines.Add($"{roll} → SL {slText} ({tag})");
        }

        return new RollResult
        {
            Kind = RollKind.Warhammer,
            Canonical = canonical,
            Warhammer = new WarhammerResult
            {
                Target = target,
                RollLines = lines
            }
        };
    }

    private static bool IsDoubles(int r)
    {
        if (r < 11 || r > 99) return false;
        int tens = (r / 10) % 10;
        int ones = r % 10;
        return tens == ones;
    }

    // -------- Arithmetic (NdS[!!], integers, + - * /) --------
    private RollResult EvaluateArithmetic(string compact)
    {
        var tokens = Tokenize(compact);
        if (tokens.Count == 0) throw new ArgumentException("Invalid expression.");

        // Build values/operators lists
        var values = new List<int>();
        var ops = new List<char>();
        foreach (var t in tokens)
        {
            if (t.Kind == TokKind.Value) values.Add(t.Value);
            else ops.Add(t.Op);
        }
        if (values.Count == 0 || values.Count != ops.Count + 1)
            throw new ArgumentException("Malformed arithmetic expression.");

        // First pass: * and /
        var vStack = new List<int> { values[0] };
        var oStack = new List<char>();
        var iVal = 1;
        for (int i = 0; i < ops.Count; i++)
        {
            char op = ops[i];
            int b = values[iVal++];
            if (op == '*' || op == '/')
            {
                int a = vStack[^1];
                vStack[^1] = op == '*' ? checked(a * b) : (b == 0 ? throw new DivideByZeroException() : a / b);
            }
            else
            {
                vStack.Add(b);
                oStack.Add(op);
            }
        }

        // Second pass: + and -
        int total = vStack[0];
        for (int i = 0; i < oStack.Count; i++)
            total = oStack[i] == '+' ? checked(total + vStack[i + 1]) : checked(total - vStack[i + 1]);

        // Pretty breakdown
        var breakdown = BuildBreakdown(tokens);

        return new RollResult
        {
            Kind = RollKind.Arithmetic,
            Canonical = compact,
            Total = total,
            Breakdown = breakdown
        };
    }

    private enum TokKind { Value, Op }
    private sealed record Tok(TokKind Kind, int Value = 0, char Op = '\0', string? Desc = null);

    private List<Tok> Tokenize(string compact)
    {
        var list = new List<Tok>();
        foreach (Match m in TokenRe.Matches(compact))
        {
            if (m.Groups["dice"].Success)
            {
                int cnt = string.IsNullOrEmpty(m.Groups["cnt"].Value) ? 1 : int.Parse(m.Groups["cnt"].Value);
                int sides = int.Parse(m.Groups["sides"].Value);
                bool explode = m.Groups["bang"].Success;

                var (sum, detail) = RollDice(cnt, sides, explode);
                list.Add(new Tok(TokKind.Value, sum, '\0', $"{cnt}d{sides}{(explode ? "!!" : "")} → {detail} = {sum}"));
            }
            else if (m.Groups["num"].Success)
            {
                int num = int.Parse(m.Groups["num"].Value);
                list.Add(new Tok(TokKind.Value, num, '\0', num.ToString()));
            }
            else if (m.Groups["op"].Success)
            {
                char op = m.Groups["op"].Value[0];
                list.Add(new Tok(TokKind.Op, 0, op));
            }
        }
        return list;
    }

    private static string BuildBreakdown(IEnumerable<Tok> toks)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var t in toks)
        {
            if (t.Kind == TokKind.Op)
            {
                sb.Append(' ').Append(t.Op).Append(' ');
            }
            else
            {
                sb.Append(t.Desc ?? t.Value.ToString());
                first = false;
            }
        }
        return sb.ToString();
    }

    private static (int sum, string detail) RollDice(int count, int sides, bool explode)
    {
        if (count < 1 || sides < 1) throw new ArgumentException("Dice count and sides must be >= 1.");

        var chunks = new List<string>();
        int sum = 0;

        for (int i = 0; i < count; i++)
        {
            if (!explode)
            {
                int r = RollInt(1, sides + 1);
                sum += r;
                chunks.Add(r.ToString());
            }
            else
            {
                var chain = new List<int>();
                while (true)
                {
                    int r = RollInt(1, sides + 1);
                    chain.Add(r);
                    sum += r;
                    if (r != sides) break; // explode on max face
                }
                chunks.Add("[" + string.Join(" + ", chain.Select(x => x == sides ? $"{x}!" : x.ToString())) + "]");
            }
        }
        return (sum, "[" + string.Join(", ", chunks) + "]");
    }

    /// <summary>Cryptographically secure, uniform integer in [minInclusive, maxExclusive).</summary>
    private static int RollInt(int minInclusive, int maxExclusive)
        => RandomNumberGenerator.GetInt32(minInclusive, maxExclusive);
}