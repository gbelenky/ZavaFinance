using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ZavaFinance.Contracts;
using ZavaFinance.Core.Finance;

namespace ZavaFinance.Core.Agent;

internal static class ClarificationSelection
{
    private static readonly string[] Ordinals =
        ["first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth"];

    public static bool IsReset(string text) =>
        text.Trim().ToLowerInvariant() is "reset" or "/reset" or "start over" or "cancel" or "never mind";

    public static bool TryRead(
        string text, PendingClarification? pending, out ClarificationSubmission? submission)
    {
        submission = null;
        string value = text.Trim().TrimEnd('.', '!').ToLowerInvariant();
        Match match = Regex.Match(value,
            @"\A(?:the\s+)?(?:option\s+)?([0-9]{1,3}(?:st|nd|rd|th)?|first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth)(?:\s+(?:one|option))?\z",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success)
        {
            if (pending is null) return false;
            if (pending.CandidateIds.Contains(text.Trim(), StringComparer.Ordinal))
            {
                submission = new(pending.RequestId, text.Trim(), pending.CatalogVersion);
                return true;
            }
            IReadOnlyList<string>? labels = pending.CandidateLabelHashes;
            IReadOnlyList<IReadOnlyList<string>>? terms = pending.CandidateTermHashes;
            if (labels?.Count != pending.CandidateIds.Count && terms?.Count != pending.CandidateIds.Count)
                return false;
            HashSet<string> hashes = SelectionHashes(text, pending.Field);
            int[] matches = Enumerable.Range(0, pending.CandidateIds.Count).Where(index =>
                (labels?.Count == pending.CandidateIds.Count && hashes.Contains(labels[index]))
                || (terms?.Count == pending.CandidateIds.Count && terms[index] is { } candidateTerms
                    && candidateTerms.Any(hashes.Contains))).ToArray();
            if (matches.Length == 0) return false;
            if (matches.Length == 1)
                submission = new(pending.RequestId, pending.CandidateIds[matches[0]], pending.CatalogVersion);
            return true;
        }
        string choice = match.Groups[1].Value;
        int number = Array.IndexOf(Ordinals, choice) + 1;
        if (number == 0)
            int.TryParse(new string(choice.TakeWhile(char.IsDigit).ToArray()),
                NumberStyles.None, CultureInfo.InvariantCulture, out number);
        if (pending is not null && number >= 1 && number <= pending.CandidateIds.Count)
            submission = new(pending.RequestId, pending.CandidateIds[number - 1], pending.CatalogVersion);
        return true;
    }

    private static HashSet<string> SelectionHashes(string text, string field)
    {
        HashSet<string> hashes = [HashLabel(text)];
        if (field == "kpi")
        {
            // Only the complete trailing percentage unit is optional, never arbitrary name prefixes.
            // Compare both forms across every option so unit/name/alias collisions stay ambiguous.
            const string suffix = " percent";
            string term = ResolverNormalization.Normalize(text);
            if (term.EndsWith(suffix, StringComparison.Ordinal))
                term = term[..^suffix.Length];
            if (term.Length > 0)
            {
                hashes.Add(HashLabel(term));
                hashes.Add(HashLabel(term + suffix));
            }
        }
        return hashes;
    }

    // Bind displayed labels and authorized catalogue terms without retaining their text in pending state.
    internal static string HashLabel(string label) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            ResolverNormalization.Normalize(label))));
}
