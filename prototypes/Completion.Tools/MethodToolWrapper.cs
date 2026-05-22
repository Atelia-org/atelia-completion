using System.Reflection;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

/// <summary>
/// 把带有 <see cref="ToolAttribute"/> 与 <see cref="ToolParamAttribute"/> 注解的方法包装为 <see cref="ITool"/> 实例。
/// </summary>
public sealed partial class MethodToolWrapper : ITool {

    public static MethodToolWrapper FromDelegate(Delegate methodDelegate) {
        if (methodDelegate is null) { throw new ArgumentNullException(nameof(methodDelegate)); }

        var invocationList = methodDelegate.GetInvocationList();
        if (invocationList.Length != 1) { throw new ArgumentException("Delegate must reference exactly one method.", nameof(methodDelegate)); }

        var singleDelegate = invocationList[0];
        return FromMethod(singleDelegate.Target, singleDelegate.Method);
    }

    public static MethodToolWrapper FromDelegate(
        Func<CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate
    ) => FromDelegate((Delegate)methodDelegate);

    public static MethodToolWrapper FromDelegate(
        Func<ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate
    ) => FromDelegate((Delegate)methodDelegate);

    public static MethodToolWrapper FromDelegate<T1>(
        Func<T1, CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate
    ) => FromDelegate((Delegate)methodDelegate);

    public static MethodToolWrapper FromDelegate<T1>(
        Func<T1, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate
    ) => FromDelegate((Delegate)methodDelegate);

    public static MethodToolWrapper FromDelegate<T1, T2>(
        Func<T1, T2, CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate
    ) => FromDelegate((Delegate)methodDelegate);

    public static MethodToolWrapper FromDelegate<T1, T2>(
        Func<T1, T2, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate
    ) => FromDelegate((Delegate)methodDelegate);

    public static MethodToolWrapper FromDelegate<T1, T2, T3>(
        Func<T1, T2, T3, CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate
    ) => FromDelegate((Delegate)methodDelegate);

    public static MethodToolWrapper FromDelegate<T1, T2, T3>(
        Func<T1, T2, T3, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate
    ) => FromDelegate((Delegate)methodDelegate);

    public static MethodToolWrapper FromDelegate<T1, T2, T3, T4>(
        Func<T1, T2, T3, T4, CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate
    ) => FromDelegate((Delegate)methodDelegate);

    public static MethodToolWrapper FromDelegate<T1, T2, T3, T4>(
        Func<T1, T2, T3, T4, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate
    ) => FromDelegate((Delegate)methodDelegate);

    public static MethodToolWrapper FromDelegate<T1, T2, T3, T4, T5>(
        Func<T1, T2, T3, T4, T5, CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate
    ) => FromDelegate((Delegate)methodDelegate);

    public static MethodToolWrapper FromDelegate<T1, T2, T3, T4, T5>(
        Func<T1, T2, T3, T4, T5, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate
    ) => FromDelegate((Delegate)methodDelegate);

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

[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class ToolParamAttribute : Attribute {
    private readonly string _description;

    public ToolParamAttribute(string description) {
        if (string.IsNullOrWhiteSpace(description)) { throw new ArgumentException("Parameter description cannot be null or whitespace.", nameof(description)); }

        _description = description.Trim();
    }

    public string Description => _description;
}
