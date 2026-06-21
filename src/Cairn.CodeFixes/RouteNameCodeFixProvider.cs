using System.Collections.Immutable;
using System.Composition;
using System.Linq;
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
        var diagnostic = context.Diagnostics[0];
        if (!diagnostic.Properties.TryGetValue("suggestion", out var suggestion) || string.IsNullOrEmpty(suggestion))
        {
            return;
        }

        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (FindLiteral(root, diagnostic.Location.SourceSpan) is not { } literal)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: $"Change route name to '{suggestion}'",
                createChangedDocument: cancellationToken => ReplaceAsync(context.Document, literal, suggestion!, cancellationToken),
                equivalenceKey: "CairnUseSuggestedRouteName"),
            diagnostic);
    }

    private static LiteralExpressionSyntax? FindLiteral(SyntaxNode? root, Microsoft.CodeAnalysis.Text.TextSpan span)
    {
        var node = root?.FindNode(span, getInnermostNodeForTie: true);
        var literal = node as LiteralExpressionSyntax
            ?? node?.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>().FirstOrDefault();
        return literal is not null && literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal : null;
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
