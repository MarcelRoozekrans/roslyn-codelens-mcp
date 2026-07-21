using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Analysis;

/// <summary>
/// The solution-wide scan for <c>IServiceCollection</c> registrations of a type, shared by
/// <c>get_di_registrations</c> and <c>get_instantiation_options</c>.
/// </summary>
public static class DiRegistrationScanner
{
    private static readonly HashSet<string> DiMethodNames = new(StringComparer.Ordinal)
    {
        "AddTransient", "AddScoped", "AddSingleton",
        "TryAddTransient", "TryAddScoped", "TryAddSingleton",
        "AddKeyedTransient", "AddKeyedScoped", "AddKeyedSingleton"
    };

    /// <summary>
    /// Recorded when a factory lambda's body is not a plain <c>new</c>: the service IS registered,
    /// so dropping the row would be wrong, but naming an implementation would be a guess.
    /// </summary>
    private const string UnresolvedFactory = "(factory)";

    public static IReadOnlyList<DiRegistration> Scan(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var results = new List<DiRegistration>();

        // Scoped by PROJECT, not by tree alone. A registration is a per-project fact: two projects
        // linking one file each genuinely register the service, and the scanner's dedupe is
        // first-one-wins, so tree-identity alone would award the file to whichever project sorts
        // first and delete the other's registrations outright. Measured, not reasoned: with the
        // discriminator dropped, Two_projects_sharing_a_file_path... reports 1 instead of 2.
        foreach (var scanTree in SolutionScanner.EnumerateTrees(
                     loaded, resolver,
                     scopeDiscriminator: static (projectName, _) => projectName,
                     cancellationToken: cancellationToken))
        {
            // Syntactic pre-filter: binding is the expensive half of the scan, and a file with no
            // Add*/TryAdd* call by name can hold no registration whatever it binds to.
            var invocations = scanTree.Root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => GetMethodName(i) is { } name && DiMethodNames.Contains(name))
                .ToList();
            if (invocations.Count == 0) continue;

            var semanticModel = scanTree.SemanticModel();
            foreach (var invocation in invocations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var methodName = GetMethodName(invocation)!;
                if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol
                    is not IMethodSymbol methodSymbol)
                    continue;

                if (Describe(invocation, methodSymbol, semanticModel, cancellationToken)
                    is not ({ } serviceName, { } implementationName))
                    continue;

                if (!MatchesSymbol(serviceName, symbol) && !MatchesSymbol(implementationName, symbol))
                    continue;

                var lineSpan = scanTree.Tree.GetLineSpan(invocation.Span, cancellationToken);
                results.Add(new DiRegistration(
                    serviceName,
                    implementationName,
                    ExtractLifetime(methodName),
                    lineSpan.Path,
                    lineSpan.StartLinePosition.Line + 1));
            }
        }

        return results;
    }

    /// <summary>
    /// The (service, implementation) pair a registration call expresses, or null when the call is
    /// not one this scanner can read.
    /// </summary>
    private static (string Service, string Implementation)? Describe(
        InvocationExpressionSyntax invocation,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var typeArgs = methodSymbol.TypeArguments;

        if (typeArgs.Length >= 2)
            return (typeArgs[0].ToDisplayString(), typeArgs[1].ToDisplayString());

        if (typeArgs.Length == 1)
        {
            var service = typeArgs[0].ToDisplayString();
            // `AddSingleton<IFoo>(sp => new Foo())` names its implementation nowhere in the type
            // arguments — only in the lambda body. Without this the row would claim IFoo registers
            // itself, which is both wrong and the opposite of useful for a "who implements this".
            var fromFactory = ResolveFactoryImplementation(invocation, semanticModel, cancellationToken);
            return (service, fromFactory ?? service);
        }

        return DescribeTypeOfPair(invocation, semanticModel, cancellationToken);
    }

    /// <summary>
    /// The non-generic <c>Add*(typeof(IFoo), typeof(Foo))</c> overloads. Only <c>typeof</c>
    /// arguments are considered, in source order, so the keyed overloads — whose key sits BETWEEN
    /// the two types — read correctly without a separate case.
    /// </summary>
    private static (string Service, string Implementation)? DescribeTypeOfPair(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var typeOfs = invocation.ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<TypeOfExpressionSyntax>()
            .ToList();
        if (typeOfs.Count is not (1 or 2)) return null;

        var service = semanticModel.GetTypeInfo(typeOfs[0].Type, cancellationToken).Type;
        if (service is null) return null;

        if (typeOfs.Count == 1)
        {
            var single = service.ToDisplayString();
            return (single, single);
        }

        var implementation = semanticModel.GetTypeInfo(typeOfs[1].Type, cancellationToken).Type;
        return implementation is null
            ? null
            : (service.ToDisplayString(), implementation.ToDisplayString());
    }

    /// <summary>
    /// The implementation a factory argument constructs, or null when the call has no factory
    /// argument at all (a plain <c>AddScoped&lt;Foo&gt;()</c>, which registers itself).
    /// </summary>
    private static string? ResolveFactoryImplementation(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var lambda = invocation.ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<AnonymousFunctionExpressionSyntax>()
            .FirstOrDefault();
        if (lambda is null) return null;

        // A statement-bodied lambda, a method call, a conditional — anything but a `new` — is left
        // unresolved rather than guessed at.
        if (lambda.ExpressionBody is not BaseObjectCreationExpressionSyntax creation)
            return UnresolvedFactory;

        return semanticModel.GetTypeInfo(creation, cancellationToken).Type?.ToDisplayString()
               ?? UnresolvedFactory;
    }

    private static string? GetMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => null
        };
    }

    private static bool MatchesSymbol(string fullName, string symbol)
    {
        if (symbol.Contains('.', StringComparison.Ordinal))
            return string.Equals(fullName, symbol, StringComparison.Ordinal);

        var lastDot = fullName.LastIndexOf('.');
        if (lastDot >= 0)
            return string.Compare(fullName, lastDot + 1, symbol, 0, symbol.Length, StringComparison.Ordinal) == 0
                   && fullName.Length - lastDot - 1 == symbol.Length;
        return string.Equals(fullName, symbol, StringComparison.Ordinal);
    }

    private static string ExtractLifetime(string methodName)
    {
        var name = methodName;
        if (name.StartsWith("TryAdd", StringComparison.Ordinal))
            name = name.Substring(6);
        else if (name.StartsWith("Add", StringComparison.Ordinal))
            name = name.Substring(3);

        if (name.StartsWith("Keyed", StringComparison.Ordinal))
            name = name.Substring(5);

        return name;
    }
}
