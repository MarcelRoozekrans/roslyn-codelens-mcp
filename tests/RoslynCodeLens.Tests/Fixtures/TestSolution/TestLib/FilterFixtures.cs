using System;
using System.CodeDom.Compiler;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.ComponentModel.Composition
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field)]
    internal class ExportAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    internal class ImportAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Constructor)]
    internal class ImportingConstructorAttribute : Attribute { }
}

namespace ModelContextProtocol.Server
{
    [AttributeUsage(AttributeTargets.Class)]
    internal class McpServerToolTypeAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    internal class McpServerToolAttribute : Attribute
    {
        public string? Name { get; set; }
    }
}

// Stand-ins for the BenchmarkDotNet attributes, so the fixture needs no NuGet package.
namespace BenchmarkDotNet.Attributes
{
    [AttributeUsage(AttributeTargets.Method)]
    internal class BenchmarkAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    internal class GlobalSetupAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    internal class GlobalCleanupAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    internal class IterationSetupAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    internal class IterationCleanupAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    internal class ParamsAttribute : Attribute
    {
        public ParamsAttribute(params object[] values) { }
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    internal class ParamsSourceAttribute : Attribute
    {
        public ParamsSourceAttribute(string name) { }
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    internal class ParamsAllValuesAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    internal class ArgumentsAttribute : Attribute
    {
        public ArgumentsAttribute(params object[] values) { }
    }

    [AttributeUsage(AttributeTargets.Method)]
    internal class ArgumentsSourceAttribute : Attribute
    {
        public ArgumentsSourceAttribute(string name) { }
        public ArgumentsSourceAttribute(Type type, string name) { }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    internal class BenchmarkCategoryAttribute : Attribute
    {
        public BenchmarkCategoryAttribute(params string[] categories) { }
    }
}

namespace TestLib.FilterFixtures
{
    using System.Collections.Generic;
    using System.ComponentModel.Composition;
    using BenchmarkDotNet.Attributes;
    using ModelContextProtocol.Server;

    // --- Generated-code filter targets ---

    [CompilerGenerated]
    public class GeneratedClass
    {
        public void NeverReferenced() { }
    }

    public class HostClass
    {
        [GeneratedCode("MyGen", "1.0")]
        public void GeneratedMember() { }
    }

    // --- MEF composition filter targets ---

    [Export]
    public class ExportedService
    {
        [ImportingConstructor]
        public ExportedService() { }
    }

    public class ImportHost
    {
        [Import] public ExportedService? Service { get; set; }
    }

    // --- Interop filter targets ---

    [StructLayout(LayoutKind.Sequential)]
    public struct InteropStruct
    {
        [MarshalAs(UnmanagedType.I4)] public int Header;
        public int PlainFieldInLaidOutStruct;
    }

    // Reference InteropStruct so the type is not flagged as an unused type
    // and its fields are walked by the filter (which only acts on fields).
    public class InteropConsumer
    {
        public InteropStruct CreateDefault() => default;
    }

    // --- MCP filter targets ---

    [McpServerToolType]
    public static class SyntheticMcpTool
    {
        [McpServerTool(Name = "synthetic")]
        public static string Execute() => "ok";
    }

    // --- BenchmarkDotNet filter targets ---

    // Nothing references this class: BenchmarkDotNet discovers it by reflection,
    // for example through BenchmarkSwitcher.FromAssembly.
    public class DiscoveredBenchmarks
    {
        [Benchmark] public void Discovered() { }

        public void UnusedBenchmarkHelper() { }
    }

    public class SyntheticBenchmarks
    {
        [Params(1, 2)] public int Size { get; set; }

        [ParamsAllValues] public bool Flag;

        [ParamsSource(nameof(NameofParamValues))] public int FromNameofParams { get; set; }

        [ParamsSource("StringParamValues")] public int FromStringParams { get; set; }

        public IEnumerable<int> NameofParamValues => new[] { 1 };

        public IEnumerable<int> StringParamValues => new[] { 1 };

        [GlobalSetup] public void Setup() { }

        [GlobalCleanup] public void Cleanup() { }

        [IterationSetup] public void IterSetup() { }

        [IterationCleanup] public void IterCleanup() { }

        [Benchmark, BenchmarkCategory("fast")] public void Run() { }

        [Benchmark, Arguments(1), Arguments(2)] public void WithArguments(int value) { }

        [Benchmark, ArgumentsSource(nameof(NameofArgs))] public void FromNameofArgs(int value) { }

        [Benchmark, ArgumentsSource("StringArgs")] public void FromStringArgs(int value) { }

        [Benchmark, ArgumentsSource(typeof(ExternalBenchmarkArgs), "Values")] public void FromOtherType(int value) { }

        public IEnumerable<int> NameofArgs() => new[] { 1 };

        public IEnumerable<int> StringArgs() => new[] { 1 };
    }

    public class ExternalBenchmarkArgs
    {
        public static IEnumerable<int> Values() => new[] { 1 };
    }
}
