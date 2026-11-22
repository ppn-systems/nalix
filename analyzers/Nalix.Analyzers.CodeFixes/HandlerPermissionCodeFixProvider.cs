// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;

namespace Nalix.Analyzers.CodeFixes;

/// <summary>
/// Adds <c>[PacketPermission(PermissionLevel.USER)]</c> to a handler that has none (NALIX111).
/// USER is the level of a bare <c>[PacketPermission]</c>; use a lower or higher level when the handler needs it.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(HandlerPermissionCodeFixProvider)), Shared]
public sealed class HandlerPermissionCodeFixProvider : CodeFixProvider
{
    private const string Title = "Add [PacketPermission(PermissionLevel.USER)]";
    private const string EquivalenceKey = "Nalix.PacketPermission.Add";

    public override ImmutableArray<string> FixableDiagnosticIds => ["NALIX111"];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        Diagnostic diagnostic = context.Diagnostics[0];
        SyntaxNode? node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        MethodDeclarationSyntax? method = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (method is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: Title,
                createChangedDocument: cancellationToken => AddPacketPermissionAsync(context.Document, method, cancellationToken),
                equivalenceKey: EquivalenceKey),
            diagnostic);
    }

    private static async Task<Document> AddPacketPermissionAsync(Document document, MethodDeclarationSyntax method, CancellationToken cancellationToken)
    {
        DocumentEditor editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);

        AttributeListSyntax attributeList = SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Attribute(SyntaxFactory.IdentifierName("PacketPermission"))
                    .WithArgumentList(
                        SyntaxFactory.AttributeArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.AttributeArgument(
                                    SyntaxFactory.ParseExpression("global::Nalix.Abstractions.Security.PermissionLevel.USER")))))))
            .WithAdditionalAnnotations(Formatter.Annotation);

        editor.ReplaceNode(method, method.AddAttributeLists(attributeList));
        return editor.GetChangedDocument();
    }
}
