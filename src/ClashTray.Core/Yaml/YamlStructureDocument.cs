namespace ClashTray.Core;

internal sealed record YamlRootNode(string Key, string Value, YamlNodeRange Section);

// Shared structural evidence. Section spans include comments and, specifically
// for listeners, indentationless sequences. Replacement spans use the stricter
// managed-node policy so neighboring comments and unknown source text survive.
internal sealed record YamlStructureDocument(
    IReadOnlyList<string> Lines,
    IReadOnlyList<YamlRootNode> Roots,
    IReadOnlyList<int> UnresolvedRoots,
    YamlDocumentScope Scope,
    string? ScopeError)
{
    public YamlDocumentScope RequireMappingScope()
    {
        if (ScopeError is not null) { throw new InvalidDataException(ScopeError); }
        return Scope;
    }

    public YamlNodeRange ReplacementSpan(YamlRootNode node, CancellationToken cancellationToken) =>
        YamlStructure.FindYamlNodeRange(Lines, node.Section.FirstLine, node.Key, cancellationToken);

    public static YamlStructureDocument Read(IReadOnlyList<string> lines, CancellationToken cancellationToken, bool allowListenerSequence = false)
    {
        ArgumentNullException.ThrowIfNull(lines);
        cancellationToken.ThrowIfCancellationRequested();
        if (lines.Count > RuntimeConfigBuilder.MaximumInputLines || lines.Any(line => line.Length > RuntimeConfigBuilder.MaximumLineCharacters))
        { throw new InvalidDataException("YAML exceeds its structural line limits."); }
        YamlDocumentScope scope = default;
        string? error = null;
        try { scope = YamlStructure.ValidateYamlDocumentScope(lines.ToArray(), cancellationToken, allowListenerSequence); }
        catch (InvalidDataException exception) { error = exception.Message; }
        List<YamlRootNode> roots = [];
        List<int> unresolved = [];
        for (int index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!YamlStructure.TryGetRootKey(lines[index], out string key))
            {
                if (YamlStructure.IsUnresolvedRootMapping(lines[index])) { unresolved.Add(index); }
                continue;
            }
            int end = index + 1;
            while (end < lines.Count && (!YamlStructure.IsUnresolvedRootMapping(lines[end]) || key == "listeners" && YamlStructure.IsSequenceEntry(lines[end])))
            {
                if ((end & 0x3F) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                end++;
            }
            roots.Add(new(key, YamlStructure.GetRootValue(lines[index]), new(index, end - 1)));
            index = end - 1;
        }
        return new(lines, roots, unresolved, scope, error);
    }
}
