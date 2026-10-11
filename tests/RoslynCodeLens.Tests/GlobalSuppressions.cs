// EPS06: ImmutableArray LINQ in test code — allocations irrelevant in test context
using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("ErrorProne.NET", "EPS06:HiddenStructCopy", Justification = "ImmutableArray LINQ allocations are acceptable in test code")]
