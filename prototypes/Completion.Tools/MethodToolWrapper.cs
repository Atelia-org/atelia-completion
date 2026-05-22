using System.Reflection;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

/// <summary>
/// 把带有 <see cref="ToolAttribute"/> 注解、并采用“单输入对象 + <see cref="ToolExecutionContext"/> + <see cref="CancellationToken"/>”签名的方法包装为 <see cref="ITool"/> 实例。
/// </summary>
public sealed partial class MethodToolWrapper : ITool {

    public static MethodToolWrapper FromDelegate(Delegate methodDelegate) {
        if (methodDelegate is null) { throw new ArgumentNullException(nameof(methodDelegate)); }

        var invocationList = methodDelegate.GetInvocationList();
        if (invocationList.Length != 1) { throw new ArgumentException("Delegate must reference exactly one method.", nameof(methodDelegate)); }

        var singleDelegate = invocationList[0];
        return FromMethod(singleDelegate.Target, singleDelegate.Method);
    }

    public static MethodToolWrapper FromDelegate<TInput>(
        Func<TInput, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate
    ) where TInput : class
        => FromDelegate((Delegate)methodDelegate);

    public static MethodToolWrapper FromMethod(object? targetInstance, MethodInfo method) => FromMethodImpl(targetInstance, method);

    public ToolDefinition Definition => _definition;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolAttribute : Attribute {
    private readonly string _name;
    private readonly string _description;

    public ToolAttribute(string name, string description) {
        if (string.IsNullOrWhiteSpace(name)) { throw new ArgumentException("Tool name cannot be null or whitespace.", nameof(name)); }

        if (string.IsNullOrWhiteSpace(description)) { throw new ArgumentException("Tool description cannot be null or whitespace.", nameof(description)); }

        _name = name.Trim();
        _description = description.Trim();
    }

    public string Name => _name;

    public string Description => _description;
}
