namespace TestLib2;

using System;

public class ReflectionUser
{
    public object? CreateDynamic(string typeName)
    {
        var type = Type.GetType(typeName);
        return type != null ? Activator.CreateInstance(type) : null;
    }
}
