using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cairn.CodeFixes;

/// <summary>Offers to replace an unknown link route name (CAIRN001) with the closest matching endpoint name.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RouteNameCodeFixProvider)), Shared]
public sealed class RouteNameCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = "CAIRN001";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticId);

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

        // The context can carry several diagnostics for the same span; offer a fix for each one.
        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.TryGetValue("suggestion", out var suggestion) || string.IsNullOrEmpty(suggestion))
            {
                continue;
            }

            // root is non-null for a C# code-fix document (see FindLiteral).
            if (FindLiteral(root!, diagnostic.Location.SourceSpan) is not { } literal)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: $"Change route name to '{suggestion}'",
                    createChangedDocument: cancellationToken => ReplaceAsync(context.Document, literal, suggestion!, cancellationToken),
                    equivalenceKey: "CairnUseSuggestedRouteName:" + suggestion),
                diagnostic);
        }
    }

    private static LiteralExpressionSyntax? FindLiteral(SyntaxNode root, Microsoft.CodeAnalysis.Text.TextSpan span)
    {
        // A C# code fix always runs on a document with a syntax root, and the diagnostic span is always within
        // that tree, so FindNode returns a node (the root at worst) — never null.
        var node = root.FindNode(span, getInnermostNodeForTie: true);

        // The common case: the diagnostic points straight at the string literal argument.
        if (node is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return literal;
        }

        // A broader span — a third-party analyzer or older Cairn version reporting on the whole invocation —
        // is fixable only when it wraps EXACTLY ONE string literal. A concatenation like "Get" + "Order"
        // wraps several, and rewriting just the first ("GetOrders" + "Order") would corrupt the name, so skip
        // it rather than guess. An interpolated string has no string-literal node at all and is skipped too.
        LiteralExpressionSyntax? single = null;
        foreach (var descendant in node.DescendantNodes())
        {
            if (descendant is LiteralExpressionSyntax candidate && candidate.IsKind(SyntaxKind.StringLiteralExpression))
            {
                if (single is not null)
                {
                    return null;
                }

                single = candidate;
            }
        }

        return single;
    }

    private static async Task<Document> ReplaceAsync(Document document, LiteralExpressionSyntax literal, string value, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var replacement = SyntaxFactory
            .LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value))
            .WithTriviaFrom(literal);
        return document.WithSyntaxRoot(root!.ReplaceNode(literal, replacement));
    }
}
