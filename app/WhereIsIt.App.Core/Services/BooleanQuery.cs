using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WhereIsIt.App.Services;

/// <summary>
/// Boolean expression tree for Everything-style grouped queries using
/// <c>&lt;</c> … <c>&gt;</c>. Only built when a query actually contains a
/// grouping bracket; otherwise the engines use the flat <see cref="SearchClause"/>
/// model unchanged, so normal queries never touch this path.
///
/// Grammar (operators: space = AND, <c>|</c> = OR, <c>!</c> = NOT,
/// <c>&lt; &gt;</c> = group):
/// <code>
/// or    := and ( '|' and )*
/// and   := unary ( unary )*
/// unary := '!' unary | primary
/// primary := '&lt;' or '&gt;' | TERM
/// </code>
/// Function tokens (anything containing <c>:</c>, e.g. <c>ext:cs</c>) are left
/// to the global filter parser and skipped here, so place functions outside
/// groups (<c>ext:cs &lt;a|b&gt;</c>) — functions inside a group are not modelled.
/// </summary>
public abstract record BoolExpr;

/// <summary>A leaf: OR of substring alternatives (matched like a normal term).</summary>
public sealed record BoolTerm(IReadOnlyList<string> Alternatives) : BoolExpr;
/// <summary>A leaf carrying a function token (e.g. <c>ext:cs</c>, <c>size:&gt;1mb</c>)
/// evaluated by re-parsing it and reusing the engine's per-row filter logic.</summary>
public sealed record BoolFunc(string Token) : BoolExpr;
public sealed record BoolNot(BoolExpr Inner) : BoolExpr;
public sealed record BoolAnd(IReadOnlyList<BoolExpr> Parts) : BoolExpr;
public sealed record BoolOr(IReadOnlyList<BoolExpr> Parts) : BoolExpr;

public static class BooleanQuery
{
    /// <summary>
    /// Parses the grouped boolean structure of <paramref name="rawQuery"/>.
    /// Returns <c>null</c> when there is no usable grouping (the caller then
    /// keeps the existing flat-clause behaviour).
    /// </summary>
    public static BoolExpr? TryParse(string rawQuery)
    {
        if (string.IsNullOrEmpty(rawQuery)) return null;
        var atoms = Lex(rawQuery);
        // Engage for an explicit group (< >) or a top-level OR operator (a
        // standalone '|' — e.g. "ext:cs | ext:txt"). A query with neither has
        // nothing for this parser to add over the flat clause model.
        if (!atoms.Any(a => a.Kind is Tok.LParen or Tok.RParen or Tok.Or)) return null;

        int pos = 0;
        var expr = ParseOr(atoms, ref pos);
        return expr;
    }

    /// <summary>Evaluates the tree, delegating each leaf (a <see cref="BoolTerm"/>
    /// or <see cref="BoolFunc"/>) to <paramref name="evalLeaf"/>.</summary>
    public static bool Eval(BoolExpr expr, Func<BoolExpr, bool> evalLeaf) => expr switch
    {
        BoolAnd a => a.Parts.All(p => Eval(p, evalLeaf)),
        BoolOr o  => o.Parts.Any(p => Eval(p, evalLeaf)),
        BoolNot n => !Eval(n.Inner, evalLeaf),
        _         => evalLeaf(expr), // BoolTerm or BoolFunc
    };

    /// <summary>Term-only evaluation: function leaves are treated as <c>true</c>
    /// so a candidate generator (the in-proc engine) returns a superset that the
    /// always-on filtering decorator then narrows with full per-row function logic.</summary>
    public static bool EvalTerms(BoolExpr expr, Func<IReadOnlyList<string>, bool> matchTerm)
        => Eval(expr, leaf => leaf is BoolTerm t ? matchTerm(t.Alternatives) : true);

    /// <summary>Collects every function-leaf token in the tree (for pre-compiling).</summary>
    public static void CollectFunctionTokens(BoolExpr expr, List<string> into)
    {
        switch (expr)
        {
            case BoolFunc f: into.Add(f.Token); break;
            case BoolNot n: CollectFunctionTokens(n.Inner, into); break;
            case BoolAnd a: foreach (var p in a.Parts) CollectFunctionTokens(p, into); break;
            case BoolOr o:  foreach (var p in o.Parts) CollectFunctionTokens(p, into); break;
        }
    }

    // ── recursive-descent parser ────────────────────────────────────────

    private static BoolExpr? ParseOr(List<Atom> atoms, ref int pos)
    {
        var parts = new List<BoolExpr>();
        var first = ParseAnd(atoms, ref pos);
        if (first is not null) parts.Add(first);
        while (pos < atoms.Count && atoms[pos].Kind == Tok.Or)
        {
            pos++;
            var next = ParseAnd(atoms, ref pos);
            if (next is not null) parts.Add(next);
        }
        return parts.Count switch { 0 => null, 1 => parts[0], _ => new BoolOr(parts) };
    }

    private static BoolExpr? ParseAnd(List<Atom> atoms, ref int pos)
    {
        var parts = new List<BoolExpr>();
        while (pos < atoms.Count && atoms[pos].Kind is Tok.Term or Tok.Func or Tok.LParen or Tok.Not)
        {
            var unary = ParseUnary(atoms, ref pos);
            if (unary is null) break;
            parts.Add(unary);
        }
        return parts.Count switch { 0 => null, 1 => parts[0], _ => new BoolAnd(parts) };
    }

    private static BoolExpr? ParseUnary(List<Atom> atoms, ref int pos)
    {
        if (pos < atoms.Count && atoms[pos].Kind == Tok.Not)
        {
            pos++;
            var inner = ParseUnary(atoms, ref pos);
            return inner is null ? null : new BoolNot(inner);
        }
        return ParsePrimary(atoms, ref pos);
    }

    private static BoolExpr? ParsePrimary(List<Atom> atoms, ref int pos)
    {
        if (pos >= atoms.Count) return null;
        var a = atoms[pos];
        if (a.Kind == Tok.LParen)
        {
            pos++;
            var inner = ParseOr(atoms, ref pos);
            if (pos < atoms.Count && atoms[pos].Kind == Tok.RParen) pos++; // tolerate a missing close
            return inner;
        }
        if (a.Kind == Tok.Term)
        {
            pos++;
            return new BoolTerm([a.Text]);
        }
        if (a.Kind == Tok.Func)
        {
            pos++;
            return new BoolFunc(a.Text);
        }
        return null; // a stray Or / RParen — let the caller stop
    }

    // ── lexer ───────────────────────────────────────────────────────────

    private enum Tok { Term, Func, Or, Not, LParen, RParen }
    private readonly record struct Atom(Tok Kind, string Text);

    private static List<Atom> Lex(string s)
    {
        var atoms = new List<Atom>();
        var word = new StringBuilder();

        void Flush()
        {
            if (word.Length == 0) return;
            var w = word.ToString();
            word.Clear();
            // Function tokens (ext:, size:, child:, …) become function leaves so
            // they can be grouped/OR'd; plain words become term leaves.
            atoms.Add(new Atom(WordHasColon(w) ? Tok.Func : Tok.Term, w));
        }

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            char next = i + 1 < s.Length ? s[i + 1] : '\0';
            switch (c)
            {
                case '"':
                    Flush();
                    int close = s.IndexOf('"', i + 1);
                    if (close < 0) break;        // lone quote — skip it
                    var literal = s.Substring(i + 1, close - (i + 1));
                    if (literal.Length > 0) atoms.Add(new Atom(Tok.Term, literal));
                    i = close;                   // loop ++ moves past the close quote
                    break;
                case ' ':
                case '\t': Flush(); break;
                case '|': Flush(); atoms.Add(new Atom(Tok.Or, "|")); break;
                case '!': Flush(); atoms.Add(new Atom(Tok.Not, "!")); break;
                case '<':
                    // Comparison operator (dm:<2020, size:<1mb) when inside a
                    // function value and followed by a value char — otherwise a
                    // grouping bracket.
                    if (WordHasColon(word) && (char.IsDigit(next) || next == '='))
                        word.Append(c);
                    else { Flush(); atoms.Add(new Atom(Tok.LParen, "<")); }
                    break;
                case '>':
                    // '>' before a digit/'=' is a comparison (size:>1kb, len:>8);
                    // otherwise it closes a group.
                    if (char.IsDigit(next) || next == '=')
                        word.Append(c);
                    else { Flush(); atoms.Add(new Atom(Tok.RParen, ">")); }
                    break;
                default: word.Append(c); break;
            }
        }
        Flush();
        return atoms;
    }

    private static bool WordHasColon(string w) => w.Contains(':');
    private static bool WordHasColon(StringBuilder w)
    {
        for (int i = 0; i < w.Length; i++) if (w[i] == ':') return true;
        return false;
    }
}
