using System.Globalization;
using System.Reflection;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

/// <summary>
/// 把带有 <see cref="ToolAttribute"/> 与 <see cref="ToolParamAttribute"/> 注解的方法包装为 <see cref="ITool"/> 实例。
/// </summary>
public sealed partial class MethodToolWrapper : ITool {

    public static MethodToolWrapper FromDelegate(Delegate methodDelegate, params object?[] formatArgs) {
        if (methodDelegate is null) { throw new ArgumentNullException(nameof(methodDelegate)); }

        var invocationList = methodDelegate.GetInvocationList();
        if (invocationList.Length != 1) { throw new ArgumentException("Delegate must reference exactly one method.", nameof(methodDelegate)); }

        var singleDelegate = invocationList[0];
        return FromMethod(singleDelegate.Target, singleDelegate.Method, formatArgs);
    }

    public static MethodToolWrapper FromDelegate(
        Func<CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate,
        params object?[] formatArgs
    ) => FromDelegate((Delegate)methodDelegate, formatArgs);

    public static MethodToolWrapper FromDelegate<T1>(
        Func<T1, CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate,
        params object?[] formatArgs
    ) => FromDelegate((Delegate)methodDelegate, formatArgs);

    public static MethodToolWrapper FromDelegate<T1, T2>(
        Func<T1, T2, CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate,
        params object?[] formatArgs
    ) => FromDelegate((Delegate)methodDelegate, formatArgs);

    public static MethodToolWrapper FromDelegate<T1, T2, T3>(
        Func<T1, T2, T3, CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate,
        params object?[] formatArgs
    ) => FromDelegate((Delegate)methodDelegate, formatArgs);

    public static MethodToolWrapper FromDelegate<T1, T2, T3, T4>(
        Func<T1, T2, T3, T4, CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate,
        params object?[] formatArgs
    ) => FromDelegate((Delegate)methodDelegate, formatArgs);

    public static MethodToolWrapper FromDelegate<T1, T2, T3, T4, T5>(
        Func<T1, T2, T3, T4, T5, CancellationToken, ValueTask<ToolExecuteResult>> methodDelegate,
        params object?[] formatArgs
    ) => FromDelegate((Delegate)methodDelegate, formatArgs);

    public static MethodToolWrapper FromMethod(object? targetInstance, MethodInfo method, params object?[] formatArgs) {
        return FromMethodImpl(targetInstance, method, formatArgs ?? Array.Empty<object?>());
    }

    public ToolDefinition Definition => _definition;

    public string Name => Definition.Name;

    public string Description => Definition.Description;

    public IReadOnlyList<ToolParamSpec> Parameters => Definition.Parameters;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolAttribute : Attribute {
    private readonly string _nameFormat;
    private readonly string _descriptionFormat;

    public ToolAttribute(string name, string description) {
        if (string.IsNullOrWhiteSpace(name)) { throw new ArgumentException("Tool name cannot be null or whitespace.", nameof(name)); }

        if (string.IsNullOrWhiteSpace(description)) { throw new ArgumentException("Tool description cannot be null or whitespace.", nameof(description)); }

        _nameFormat = name.Trim();
        _descriptionFormat = description.Trim();
    }

    public string Name => _nameFormat;

    public string Description => _descriptionFormat;

    internal string FormatName(object?[] formatArgs)
        => FormatWithArgs(_nameFormat, formatArgs);

    internal string FormatDescription(object?[] formatArgs)
        => FormatWithArgs(_descriptionFormat, formatArgs);

    private static string FormatWithArgs(string format, object?[] formatArgs) {
        if (formatArgs.Length == 0) { return format; }

        return string.Format(CultureInfo.InvariantCulture, format, formatArgs);
    }
}

[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class ToolParamAttribute : Attribute {
    private readonly string _descriptionFormat;

    public ToolParamAttribute(string description) {
        if (string.IsNullOrWhiteSpace(description)) { throw new ArgumentException("Parameter description cannot be null or whitespace.", nameof(description)); }

        _descriptionFormat = description.Trim();
    }

    public string Description => _descriptionFormat;

    internal string FormatDescription(object?[] formatArgs)
        => formatArgs.Length == 0
            ? _descriptionFormat
            : string.Format(CultureInfo.InvariantCulture, _descriptionFormat, formatArgs);
}
