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

namespace TestLib.FilterFixtures
{
    using System.ComponentModel.Composition;
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

    [StructLayout(LayoutKind.Explicit)]
    public struct InteropStruct
    {
        [FieldOffset(0)] public int Header;
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
}
