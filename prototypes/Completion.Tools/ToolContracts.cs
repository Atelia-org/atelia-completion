using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

public static class ToolContracts {
    public static ToolDefinition GetValidatedDefinition(ITool tool) {
        if (tool is null) { throw new ArgumentNullException(nameof(tool)); }
        if (tool.Definition is null) { throw new InvalidOperationException($"Tool '{tool.GetType().FullName}' returned null Definition."); }
        return tool.Definition;
    }

    public static void EnsureStableFlatProjection(ToolDefinition definition, string subject) {
        if (definition is null) { throw new ArgumentNullException(nameof(definition)); }

        if (TryExplainFlatProjectionFailure(definition, out var reason)) {
            throw new InvalidOperationException($"{subject} 的 InputSchema 当前无法稳定投影为 flat 参数。原因: {reason}");
        }
    }

    public static ToolDefinition CreateCompatibleFlatOverride(
        ToolDefinition authoritativeDefinition,
        ToolDefinition overrideDefinition
    ) {
        if (authoritativeDefinition is null) { throw new ArgumentNullException(nameof(authoritativeDefinition)); }
        if (overrideDefinition is null) { throw new ArgumentNullException(nameof(overrideDefinition)); }

        if (!SchemasAreMetadataCompatible(authoritativeDefinition.InputSchema, overrideDefinition.InputSchema)) {
            throw new InvalidOperationException(
                $"工具 '{authoritativeDefinition.Name}' 的 metadata override 不能修改 provider-visible schema 语义。"
            );
        }

        return overrideDefinition;
    }

    private static bool SchemasAreMetadataCompatible(ToolSchema authoritative, ToolSchema candidate) {
        if (authoritative.GetType() != candidate.GetType()) { return false; }

        return authoritative switch {
            ToolSchema.Object authoritativeObject when candidate is ToolSchema.Object candidateObject
                => ObjectsAreMetadataCompatible(authoritativeObject, candidateObject),
            ToolSchema.Array authoritativeArray when candidate is ToolSchema.Array candidateArray
                => authoritativeArray.IsNullable == candidateArray.IsNullable
                   && SchemasAreMetadataCompatible(authoritativeArray.ItemSchema, candidateArray.ItemSchema),
            ToolSchema.Value authoritativeValue when candidate is ToolSchema.Value candidateValue
                => ValuesAreMetadataCompatible(authoritativeValue, candidateValue),
            _ => false
        };
    }

    private static bool ObjectsAreMetadataCompatible(ToolSchema.Object authoritative, ToolSchema.Object candidate) {
        if (authoritative.AdditionalProperties != candidate.AdditionalProperties) { return false; }
        if (authoritative.Properties.Length != candidate.Properties.Length) { return false; }

        for (var i = 0; i < authoritative.Properties.Length; i++) {
            var left = authoritative.Properties[i];
            var right = candidate.Properties[i];

            if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal)) { return false; }
            if (left.IsRequired != right.IsRequired) { return false; }
            if (!SchemasAreMetadataCompatible(left.Schema, right.Schema)) { return false; }
        }

        return true;
    }

    private static bool ValuesAreMetadataCompatible(ToolSchema.Value authoritative, ToolSchema.Value candidate) {
        if (authoritative.ValueKind != candidate.ValueKind) { return false; }
        if (authoritative.IsNullable != candidate.IsNullable) { return false; }
        if (!ParamDefaultEquals(authoritative.Default, candidate.Default)) { return false; }
        if (!authoritative.StringEnumValues.SequenceEqual(candidate.StringEnumValues, StringComparer.Ordinal)) { return false; }
        if (authoritative.MinLength != candidate.MinLength) { return false; }
        if (authoritative.MaxLength != candidate.MaxLength) { return false; }
        if (!string.Equals(authoritative.Pattern, candidate.Pattern, StringComparison.Ordinal)) { return false; }
        if (!Equals(authoritative.Minimum, candidate.Minimum)) { return false; }
        if (!Equals(authoritative.Maximum, candidate.Maximum)) { return false; }
        return true;
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
