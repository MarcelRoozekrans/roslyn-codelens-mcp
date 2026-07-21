using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests;

public class DiRegistrationScannerTests
{
    /// <summary>
    /// The fixture workspace has no Microsoft.Extensions.DependencyInjection reference, so the
    /// registration API is stubbed with the real names and shapes. The scanner recognises a call
    /// by method NAME and by what the bound symbol's type arguments say, not by the declaring
    /// assembly, so a faithful stub exercises exactly the production path.
    /// </summary>
    private const string Stub = """
        using System;
        namespace Demo;

        public interface IServiceCollection {}

        public static class ServiceCollectionExtensions
        {
            public static IServiceCollection AddSingleton<TService, TImpl>(this IServiceCollection s) => s;
            public static IServiceCollection AddScoped<TImpl>(this IServiceCollection s) => s;
            public static IServiceCollection AddTransient(this IServiceCollection s, Type serviceType, Type implementationType) => s;
            public static IServiceCollection AddTransient(this IServiceCollection s, Type serviceType) => s;
            public static IServiceCollection AddSingleton<TService>(this IServiceCollection s, Func<IServiceProvider, TService> factory) => s;
        }
        """;

    private const string Startup = """
        namespace Demo;
        public interface IFoo {}
        public class Foo : IFoo {}
        public class Bar : IFoo {}
        public static class FooSource { public static Foo Get() => new Foo(); }

        public class Startup
        {
            public void Configure(IServiceCollection s)
            {
                s.AddSingleton<IFoo, Foo>();
                s.AddScoped<Foo>();
                s.AddTransient(typeof(IFoo), typeof(Foo));
                s.AddSingleton<IFoo>(sp => new Foo());
            }
        }
        """;

    private static IReadOnlyList<DiRegistration> Scan(string symbol)
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("Stub.cs", Stub), ("Startup.cs", Startup));
        return DiRegistrationScanner.Scan(loaded, resolver, symbol);
    }

    [Fact]
    public void Finds_two_type_generic_registration()
    {
        var r = Assert.Single(Scan("Foo"), x => x.Lifetime == "Singleton" && x.Service == "Demo.IFoo"
                                                && x.Line == 11);
        Assert.Equal("Demo.Foo", r.Implementation);
    }

    [Fact]
    public void Finds_single_generic_registration()
    {
        var r = Assert.Single(Scan("Foo"), x => x.Lifetime == "Scoped");
        Assert.Equal("Demo.Foo", r.Service);
        Assert.Equal("Demo.Foo", r.Implementation);
    }

    [Fact]
    public void Finds_typeof_pair_registration()
    {
        var r = Assert.Single(Scan("Foo"), x => x.Lifetime == "Transient");
        Assert.Equal("Demo.IFoo", r.Service);
        Assert.Equal("Demo.Foo", r.Implementation);
    }

    [Fact]
    public void Finds_factory_lambda_registration()
    {
        var r = Assert.Single(Scan("Foo"), x => x.Lifetime == "Singleton" && x.Line == 14);
        Assert.Equal("Demo.IFoo", r.Service);
        Assert.Equal("Demo.Foo", r.Implementation);
    }

    [Fact]
    public void Factory_lambda_with_a_non_construction_body_is_not_guessed_at()
    {
        // `sp => FooSource.Get()` hands back something the scanner cannot name without following
        // the call; reporting "Demo.Foo" here would be a guess, so the registration is recorded
        // with the implementation left explicitly unresolved.
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("Stub.cs", Stub),
            ("Startup.cs", Startup),
            ("Other.cs", """
                namespace Demo;
                public class Other
                {
                    public void Configure(IServiceCollection s) => s.AddSingleton<IFoo>(sp => FooSource.Get());
                }
                """));

        var r = Assert.Single(
            DiRegistrationScanner.Scan(loaded, resolver, "IFoo"),
            x => x.File.EndsWith("Other.cs", StringComparison.Ordinal));
        Assert.Equal("(factory)", r.Implementation);
    }

    /// <summary>
    /// The scan runs through <see cref="SolutionScanner"/>, whose dedupe is first-one-wins on
    /// (scope, tree identity). A DI registration is a PER-PROJECT fact: two projects that link the
    /// same file each genuinely register the service, so without a project-scoped discriminator the
    /// second project's registrations vanish — silently, and only on solutions with linked files.
    /// </summary>
    [Fact]
    public void Two_projects_sharing_a_file_path_do_not_lose_or_duplicate_registrations()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("ProjA", [("Stub.cs", Stub), ("Shared.cs", Startup)]),
            ("ProjB", [("Stub.cs", Stub), ("Shared.cs", Startup)]));

        var results = DiRegistrationScanner.Scan(loaded, resolver, "Foo");

        Assert.Equal(2, results.Count(r => r.Lifetime == "Scoped"));
    }

    [Fact]
    public void Non_registration_invocations_are_ignored()
    {
        Assert.Empty(Scan("Bar"));
    }

    /// <summary>
    /// Registrations emitted by a source generator count. Most scanners skip generated trees
    /// because they report things a human should go and fix; this one reports what the container
    /// actually does at startup, and a generated <c>AddScoped</c> wires it up exactly as a
    /// hand-written one does.
    /// <para>
    /// This is a REGRESSION guard, not a new feature: the hand-rolled loop this scanner replaced
    /// had no generated-code filter, so migrating to <see cref="SolutionScanner"/> — which skips
    /// them by default — silently narrowed a shipped tool. Nothing in the suite covered it, so
    /// nothing failed. Delete the <c>includeGenerated: true</c> argument and this test fails.
    /// </para>
    /// </summary>
    [Fact]
    public void Registrations_in_generated_files_are_still_found()
    {
        const string Generated = """
            namespace Demo;
            public static class GeneratedRegistrations
            {
                public static void Register(IServiceCollection s) => s.AddScoped<Foo>();
            }
            """;

        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("Stub.cs", Stub),
            ("Types.cs", "namespace Demo; public interface IFoo {} public class Foo : IFoo {}"),
            // .g.cs is what GeneratedCodeDetector keys on.
            ("Registrations.g.cs", Generated));

        Assert.Single(
            DiRegistrationScanner.Scan(loaded, resolver, "Foo"),
            r => r.File.EndsWith("Registrations.g.cs", StringComparison.Ordinal));
    }
}
