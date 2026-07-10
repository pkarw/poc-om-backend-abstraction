using OpenMercato.Modules.QueryIndex.Lib;
using Xunit;

namespace OpenMercato.Tests.QueryIndex;

/// <summary>
/// Golden-vector tests for the tokenized-search port (upstream shared/lib/search/tokenize.ts +
/// config.ts). The token strings follow the upstream algorithm (NFKD normalize → strip combining marks →
/// <c>%</c>/<c>_</c> → space → lowercase → split on <c>[^a-zA-Z0-9]+</c> → min length 3 → prefix partials);
/// the hashes are <c>sha256(token)</c> hex, pinned from the byte-for-byte reference so the .NET write side
/// produces the exact same <c>token_hash</c> values as the TypeScript build.
/// </summary>
public class SearchTokenizerTests
{
    private static readonly SearchConfig Default = new(); // enabled, minLen 3, partials on, sha256

    [Fact]
    public void Tokenize_single_word_emits_prefix_partials()
    {
        var r = SearchTokenizer.Tokenize("Acme", Default);
        Assert.Equal(new[] { "acm", "acme" }, r.Tokens);
        Assert.Equal(new[]
        {
            "62380b77e7bbd9ac3cef5b652d9ded048f2cc860fa9ebdf52b0d9bb375c9ce8f", // acm
            "822b33ad87c148a0a20a5ba7cd5ebcaa68d36a18e7aad165554903f52ca82757", // acme
        }, r.Hashes);
    }

    [Fact]
    public void Tokenize_splits_on_non_alphanumeric_and_expands_each()
    {
        var r = SearchTokenizer.Tokenize("John Doe", Default);
        Assert.Equal(new[] { "joh", "john", "doe" }, r.Tokens);
        Assert.Equal(new[]
        {
            "6f79f083a467060dee424261a0dd4d826d130d932b803ef8fb43b42a531b7d1f", // joh
            "96d9632f363564cc3032521409cf22a852f2032eec099ed5967c0d000cec607a", // john
            "799ef92a11af918e3fb741df42934f3b568ed2d93ac1df74f1b8d41a27932a6f", // doe
        }, r.Hashes);
    }

    [Fact]
    public void Tokenize_strips_diacritics_via_nfkd()
    {
        var r = SearchTokenizer.Tokenize("Café", Default);
        Assert.Equal(new[] { "caf", "cafe" }, r.Tokens);
        Assert.Equal(new[]
        {
            "0fb91c7693196ba95dba58ea9576667e66fffd63e153388ef7b7b843ef23b330", // caf
            "a860b858265b22dad3aaf1165cfc2936daf1d3d86e0b7b77e3cc07f59f96858f", // cafe
        }, r.Hashes);
    }

    [Fact]
    public void Tokenize_drops_tokens_shorter_than_min_length()
    {
        // "a" and "of" are below minTokenLength (3) and are dropped entirely.
        var r = SearchTokenizer.Tokenize("a of widget", Default);
        Assert.Equal(new[] { "wid", "widg", "widge", "widget" }, r.Tokens);
    }

    [Fact]
    public void Tokenize_without_partials_emits_full_tokens_only()
    {
        var cfg = Default with { EnablePartials = false };
        var r = SearchTokenizer.Tokenize("Acme John", cfg);
        Assert.Equal(new[] { "acme", "john" }, r.Tokens);
    }

    [Fact]
    public void Tokenize_percent_and_underscore_become_separators()
    {
        // normalizeText replaces %/_ with spaces (so ilike wildcards never leak into tokens).
        var r = SearchTokenizer.Tokenize("acme_corp%ltd", Default with { EnablePartials = false });
        Assert.Equal(new[] { "acme", "corp", "ltd" }, r.Tokens);
    }

    [Fact]
    public void HashToken_matches_reference_sha256()
    {
        Assert.Equal(
            "8ac140ceb6ca8d6e51a987a9828b9f97b95bbc3ae6bdb0558e2631cb8da232b8",
            SearchTokenizer.HashToken("widget", Default));
    }
}
