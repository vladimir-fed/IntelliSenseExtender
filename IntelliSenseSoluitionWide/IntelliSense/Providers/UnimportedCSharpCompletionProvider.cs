﻿using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntelliSenseExtender.ExposedInternals;
using IntelliSenseExtender.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntelliSenseExtender.IntelliSense.Providers
{
    [ExportCompletionProvider("Unimported provider", LanguageNames.CSharp)]
    public class UnimportedCSharpCompletionProvider : CompletionProvider
    {
        private const string NamespaceProperty = "Namespace";

        private IReadOnlyList<string> _usings;

        public override async Task ProvideCompletionsAsync(CompletionContext context)
        {
            _usings = await GetImportedNamespaces(context.Document);

            var publicClasses = await GetSymbols(context).ConfigureAwait(false);
            var completionItemsToAdd = publicClasses
                .Select(symbol => CreateCompletionItemForSymbol(symbol));

            context.AddItems(completionItemsToAdd);
        }

        public override Task<CompletionDescription> GetDescriptionAsync(Document document, CompletionItem item, CancellationToken cancellationToken)
        {
            return base.GetDescriptionAsync(document, item, cancellationToken);
        }

        private CompletionItem CreateCompletionItemForSymbol(ISymbol typeSymbol)
        {
            var accessabilityTag = typeSymbol.DeclaredAccessibility == Accessibility.Public
                ? CompletionTags.Public
                : CompletionTags.Private;
            var tags = ImmutableArray.Create(CompletionTags.Class, accessabilityTag);

            return CompletionItem.Create(
                displayText: typeSymbol.Name,
                sortText: "_" + typeSymbol.Name,
                tags: tags);
        }

        private async Task<Document> AddUsings(Document document, params string[] nameSpaces)
        {
            var syntaxRoot = await document.GetSyntaxRootAsync().ConfigureAwait(false);
            if (syntaxRoot is CompilationUnitSyntax compilationUnitSyntax)
            {
                var usingNames = nameSpaces
                    .Select(ns => SyntaxFactory.ParseName(ns))
                    .Select(nsName => SyntaxFactory.UsingDirective(nsName).NormalizeWhitespace())
                    .ToArray();
                compilationUnitSyntax = compilationUnitSyntax.AddUsings(usingNames);
                return document.WithSyntaxRoot(compilationUnitSyntax);
            }
            else
            {
                return document;
            }
        }

        private async ValueTask<IReadOnlyList<string>> GetImportedNamespaces(Document document)
        {
            var tree = await document.GetSyntaxTreeAsync().ConfigureAwait(false);
            if (tree.GetRoot() is CompilationUnitSyntax compilationUnitSyntax)
            {
                var childNodes = compilationUnitSyntax.ChildNodes().ToArray();

                var namespaces = childNodes
                    .OfType<UsingDirectiveSyntax>()
                    .Select(u => u.Name.ToString()).ToList();

                var currentNamespaces = childNodes
                    .OfType<NamespaceDeclarationSyntax>()
                    .Select(nsSyntax => nsSyntax.Name.ToString());

                namespaces.AddRange(currentNamespaces);

                return namespaces;
            }
            else
            {
                return new string[] { };
            }
        }

        private async Task<IEnumerable<ISymbol>> GetSymbols(CompletionContext context)
        {
            var document = context.Document;
            var semanticModel = await document.GetSemanticModelAsync(context.CancellationToken);
            var syntaxTree = await document.GetSyntaxTreeAsync();

            if (syntaxTree.IsTypeContext(context.Position, context.CancellationToken, semanticModel))
            {
                return GetAllTypes(semanticModel);
            }
            else
            {
                return Enumerable.Empty<ISymbol>();
            }
        }

        private List<INamedTypeSymbol> GetAllTypes(SemanticModel semanticModel)
        {
            const int typesCapacity = 100000;

            var foundTypes = new List<INamedTypeSymbol>(typesCapacity);

            var namespacesToTraverse = new[] { semanticModel.Compilation.GlobalNamespace };
            while (namespacesToTraverse.Length > 0)
            {
                var members = namespacesToTraverse.SelectMany(ns => ns.GetMembers()).ToArray();
                var typeSymbols = members
                    .OfType<INamedTypeSymbol>()
                    .Where(symbol => FilterType(symbol, semanticModel));
                foundTypes.AddRange(typeSymbols);
                namespacesToTraverse = members
                    .OfType<INamespaceSymbol>()
                    .Where(FilterNamespace)
                    .ToArray();
            }

            return foundTypes;
        }

        private bool FilterNamespace(INamespaceSymbol ns)
        {
            bool userCodeOnly = Options.Options.UserCodeOnlySuggestions;
            return (!userCodeOnly || ns.Locations.Any(l => l.IsInSource))
                 && ns.CanBeReferencedByName;
        }

        private bool FilterType(INamedTypeSymbol type, SemanticModel semanticModel)
        {
            return (type.DeclaredAccessibility == Accessibility.Public
                    || (type.DeclaredAccessibility == Accessibility.Internal
                        && type.ContainingAssembly == semanticModel.Compilation.Assembly))
                && type.CanBeReferencedByName
                && !_usings.Contains(type.GetNamespace());
        }
    }
}
