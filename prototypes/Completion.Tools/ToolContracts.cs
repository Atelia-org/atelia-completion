using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

public static class ToolContracts {
    public static ToolDefinition GetValidatedDefinition(ITool tool) {
        if (tool is null) { throw new ArgumentNullException(nameof(tool)); }
        if (tool.Definition is null) { throw new InvalidOperationException($"Tool '{tool.GetType().FullName}' returned null Definition."); }

        EnsureStableFlatProjection(tool.Definition, $"工具 '{tool.Definition.Name}'");
        return tool.Definition;
    }

    public static void EnsureStableFlatProjection(ToolDefinition definition, string subject) {
        if (definition is null) { throw new ArgumentNullException(nameof(definition)); }

        if (TryExplainFlatProjectionFailure(definition, out var reason)) {
            throw new InvalidOperationException($"{subject} 的 InputSchema 当前无法稳定投影为 flat 参数；在 parser/binder 仍是 flat-only 时禁止注册。原因: {reason}");
        }
    }

    public static ToolDefinition CreateCompatibleFlatOverride(
        ToolDefinition authoritativeDefinition,
        ToolDefinition overrideDefinition
    ) {
        if (authoritativeDefinition is null) { throw new ArgumentNullException(nameof(authoritativeDefinition)); }
        if (overrideDefinition is null) { throw new ArgumentNullException(nameof(overrideDefinition)); }

        EnsureStableFlatProjection(authoritativeDefinition, $"工具 '{authoritativeDefinition.Name}'");
        EnsureStableFlatProjection(overrideDefinition, $"工具 '{overrideDefinition.Name}'");

        var authoritativeParameters = authoritativeDefinition.Parameters;
        var overrideParameters = overrideDefinition.Parameters;

        if (authoritativeParameters.Length != overrideParameters.Length) {
            throw new InvalidOperationException(
                $"工具 '{authoritativeDefinition.Name}' 的 metadata override 参数数量不兼容：authoritative={authoritativeParameters.Length}, override={overrideParameters.Length}."
            );
        }

        for (var i = 0; i < authoritativeParameters.Length; i++) {
            var expected = authoritativeParameters[i];
            var actual = overrideParameters[i];

            if (!string.Equals(expected.Name, actual.Name, StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    $"工具 '{authoritativeDefinition.Name}' 的 metadata override 不能修改参数名：index={i}, authoritative='{expected.Name}', override='{actual.Name}'."
                );
            }

            if (expected.ValueKind != actual.ValueKind || expected.IsNullable != actual.IsNullable) {
                throw new InvalidOperationException(
                    $"工具 '{authoritativeDefinition.Name}' 的 metadata override 不能修改参数类型或 nullability：parameter='{expected.Name}'."
                );
            }

            if (expected.IsRequired != actual.IsRequired || !ParamDefaultEquals(expected.Default, actual.Default)) {
                throw new InvalidOperationException(
                    $"工具 '{authoritativeDefinition.Name}' 的 metadata override 不能修改参数 required/default：parameter='{expected.Name}'."
                );
            }
        }

        return overrideDefinition;
    }

    private static bool TryExplainFlatProjectionFailure(ToolDefinition definition, out string reason) {
        if (definition.InputSchema is not ToolSchema.Object objectSchema) {
            reason = "tool input schema root is not an object.";
            return true;
        }

        if (objectSchema.AdditionalProperties) {
            reason = "additionalProperties=true is not supported by the flat parser.";
            return true;
        }

        if (objectSchema.Properties.IsDefaultOrEmpty) {
            reason = string.Empty;
            return false;
        }

        foreach (var property in objectSchema.Properties) {
            if (property.Schema is not ToolSchema.Value valueSchema) {
                reason = $"property '{property.Name}' is not a flat value schema.";
                return true;
            }

            if (string.IsNullOrWhiteSpace(valueSchema.Description)) {
                reason = $"property '{property.Name}' is missing description required by flat projection.";
                return true;
            }

            if (!property.IsRequired && !valueSchema.Default.HasValue) {
                reason = $"property '{property.Name}' is optional without default, which ToolParamSpec cannot represent.";
                return true;
            }
        }

        if (definition.Parameters.Length != objectSchema.Properties.Length) {
            reason = "ToolDefinition.Parameters does not match the schema property count.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool ParamDefaultEquals(ParamDefault? left, ParamDefault? right) {
        if (left.HasValue != right.HasValue) { return false; }
        if (!left.HasValue) { return true; }

        var leftValue = left.GetValueOrDefault();
        var rightValue = right.GetValueOrDefault();
        return Equals(leftValue.Value, rightValue.Value);
    }
}
