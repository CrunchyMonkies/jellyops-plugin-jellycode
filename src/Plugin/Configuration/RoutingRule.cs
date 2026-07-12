namespace Jellyfin.Plugin.DistributedTranscoding.Configuration;

/// <summary>
/// A routing rule that directs jobs matching a (source decode codec, target encode codec) pair to an
/// ordered list of preferred worker types. Codec fields accept a concrete codec (<c>h264</c>,
/// <c>hevc</c>, <c>av1</c>, <c>vp9</c>, …), the literal <c>copy</c> (for <see cref="EncodeCodec"/>
/// stream-copy jobs), or <c>any</c>/empty as a wildcard. The most specific matching rule wins.
/// </summary>
public class RoutingRule
{
    /// <summary>
    /// Gets or sets the source (decode) codec to match, or <c>any</c>/empty for a wildcard.
    /// </summary>
    public string DecodeCodec { get; set; } = "any";

    /// <summary>
    /// Gets or sets the target (encode) codec to match: a concrete codec, <c>copy</c> for stream-copy
    /// jobs, or <c>any</c>/empty for a wildcard.
    /// </summary>
    public string EncodeCodec { get; set; } = "any";

    /// <summary>
    /// Gets or sets the ordered worker-type preference for jobs matching this rule, e.g.
    /// <c>["nvidia", "intel", "cpu"]</c>. The scheduler tries each in turn.
    /// </summary>
    public string[] WorkerPriority { get; set; } = System.Array.Empty<string>();

    /// <summary>
    /// Returns true when the field matches the given codec value (case-insensitive), treating
    /// <c>any</c>/empty as a wildcard.
    /// </summary>
    /// <param name="field">The rule field value (DecodeCodec/EncodeCodec).</param>
    /// <param name="codec">The job's actual codec (may be null).</param>
    /// <returns>Whether the field matches.</returns>
    public static bool FieldMatches(string field, string? codec)
    {
        if (string.IsNullOrWhiteSpace(field) || string.Equals(field, "any", System.StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(field, codec, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the specificity of this rule: 2 when both codec fields are concrete, 1 when one is, 0 for
    /// the all-wildcard rule. Higher wins when several rules match.
    /// </summary>
    /// <returns>The specificity score.</returns>
    public int Specificity()
    {
        var score = 0;
        if (!IsWildcard(DecodeCodec))
        {
            score++;
        }

        if (!IsWildcard(EncodeCodec))
        {
            score++;
        }

        return score;
    }

    private static bool IsWildcard(string field) =>
        string.IsNullOrWhiteSpace(field) || string.Equals(field, "any", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the ordered worker-type priority for a (decode, encode) codec pair: the most specific
    /// matching rule wins; falls back to <paramref name="defaultOrder"/> (or a built-in default when
    /// that is empty). Pure — used by <c>PluginConfiguration.ResolveWorkerPriority</c> and tests.
    /// </summary>
    /// <param name="rules">The configured rules (may be null/empty).</param>
    /// <param name="defaultOrder">The default order when no rule matches.</param>
    /// <param name="decodeCodec">The job's source (decode) codec, or null.</param>
    /// <param name="encodeCodec">The job's target (encode) codec, <c>copy</c>, or null.</param>
    /// <returns>The ordered worker types.</returns>
    public static string[] Resolve(System.Collections.Generic.IEnumerable<RoutingRule>? rules, string[]? defaultOrder, string? decodeCodec, string? encodeCodec)
    {
        RoutingRule? best = null;
        if (rules is not null)
        {
            foreach (var rule in rules)
            {
                if (rule.WorkerPriority is not { Length: > 0 })
                {
                    continue;
                }

                if (!FieldMatches(rule.DecodeCodec, decodeCodec) || !FieldMatches(rule.EncodeCodec, encodeCodec))
                {
                    continue;
                }

                if (best is null || rule.Specificity() > best.Specificity())
                {
                    best = rule;
                }
            }
        }

        var order = best?.WorkerPriority ?? defaultOrder;
        return order is { Length: > 0 } ? order : ["intel", "nvidia", "cpu"];
    }
}
