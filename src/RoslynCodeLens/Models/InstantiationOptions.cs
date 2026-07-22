namespace RoslynCodeLens.Models;

/// <summary>
/// One way to construct a type with <c>new</c>.
/// </summary>
/// <param name="Accessible">
/// Null when no caller context was supplied — meaning "not computed", NOT "inaccessible".
/// Only a caller project can decide accessibility, because <c>InternalsVisibleTo</c> makes the
/// answer depend on who is asking.
/// </param>
/// <param name="IsImplicit">
/// True for a constructor the compiler supplied rather than the source declaring. It is still
/// callable — a struct, and a class with no declared constructor, both have a usable
/// parameterless one — so this is information, not a disqualification.
/// </param>
public record ConstructorOption(
    string Signature,
    string Accessibility,
    bool? Accessible,
    bool IsImplicit,
    bool IsObsolete,
    IReadOnlyList<ParameterOption> Parameters,
    string? File,
    int? Line);

public record ParameterOption(string Type, string Name, bool HasDefault);

/// <summary>
/// A static member anywhere in the solution that hands back an instance of the type.
/// </summary>
/// <param name="Kind"><c>method</c>, <c>property</c>, or <c>field</c>.</param>
/// <param name="IsAsync">
/// Return type was <c>Task&lt;T&gt;</c>/<c>ValueTask&lt;T&gt;</c> and has been unwrapped, so the
/// caller must await it.
/// </param>
/// <param name="Accessible">
/// Null when no caller context was supplied — meaning "not computed", NOT "inaccessible".
/// </param>
public record FactoryOption(
    string Signature,
    string DeclaringType,
    string Kind,
    string Accessibility,
    bool? Accessible,
    bool IsAsync,
    bool IsObsolete,
    IReadOnlyList<ParameterOption> Parameters,
    string? File,
    int? Line);

/// <summary>
/// A member marked <c>required</c>, which the caller must set in an object initializer.
/// </summary>
public record RequiredMemberOption(string Type, string Name);

/// <param name="Instantiable">
/// False for interfaces, static classes, and abstract classes. When false, <see cref="Constructors"/>
/// is empty even though Roslyn still exposes constructors for abstract types, and
/// <see cref="Note"/> says why.
/// </param>
public record InstantiationOptionsResult(
    string Type,
    string TypeKind,
    bool Instantiable,
    string? Note,
    IReadOnlyList<ConstructorOption> Constructors,
    IReadOnlyList<FactoryOption> Factories,
    IReadOnlyList<DiRegistration> DiRegistrations,
    IReadOnlyList<RequiredMemberOption> RequiredMembers);
