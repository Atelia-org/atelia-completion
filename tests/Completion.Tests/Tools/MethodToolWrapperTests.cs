using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Utils;
using Xunit;

namespace Atelia.Completion.Tools.Tests;

public sealed class MethodToolWrapperTests {
    [Fact]
    public async Task FromMethod_WithDtoInput_InjectsContextAndHidesInfrastructureParametersFromSchema() {
        var target = new MethodToolTarget();
        var wrapper = MethodToolWrapper.FromMethod(
            target,
            typeof(MethodToolTarget).GetMethod(nameof(MethodToolTarget.ExecuteAsync))!
        );

        var definition = wrapper.Definition;
        var inputSchema = Assert.IsType<ToolSchema.Object>(definition.InputSchema);
        var visibleProperty = Assert.Single(inputSchema.Properties);
        Assert.Equal("text", visibleProperty.Name);
        Assert.Equal("Visible text.", visibleProperty.Schema.Description);

        var providerSchema = JsonToolSchemaBuilder.BuildSchema(definition);
        var properties = providerSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(new[] { "text" }, properties);

        var session = new ToolSessionState(items: new Dictionary<string, object?> { ["scope"] = "session-scope" });
        var context = new ToolExecutionContext(
            session,
            new RawToolCall("method.with_context", "call-1", """{"text":"hello"}"""),
            executionSequence: 7
        );

        var result = await wrapper.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Success, result.Status);
        Assert.Equal("hello|session-scope|7", result.Content);
        Assert.Same(context, target.ObservedContext);
    }

    [Fact]
    public void FromMethod_RequiresSingleInputObjectFollowedByContextAndCancellationToken() {
        var target = new InvalidMethodToolTarget();

        var exception = Assert.Throws<InvalidOperationException>(
            () => MethodToolWrapper.FromMethod(
                target,
                typeof(InvalidMethodToolTarget).GetMethod(nameof(InvalidMethodToolTarget.ExecuteAsync))!
            )
        );

        Assert.Contains("exactly one business input parameter", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WhenObjectGraphValidationFails_DoesNotInvokeMethod() {
        var target = new ValidatedMethodToolTarget();
        var wrapper = MethodToolWrapper.FromMethod(
            target,
            typeof(ValidatedMethodToolTarget).GetMethod(nameof(ValidatedMethodToolTarget.ExecuteAsync))!
        );

        var context = new ToolExecutionContext(
            new ToolSessionState(),
            new RawToolCall("method.validation_failure", "call-2", """{"text":"a"}"""),
            executionSequence: 8
        );

        var result = await wrapper.ExecuteAsync(context, CancellationToken.None);

        Assert.False(target.Invoked);
        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Contains("工具参数验证失败", result.Content, StringComparison.Ordinal);
        Assert.Contains("text", result.Content, StringComparison.Ordinal);
    }

    private sealed class MethodToolTarget {
        public ToolExecutionContext? ObservedContext { get; private set; }

        [Tool("method.with_context", "Execute a method tool with context.")]
        public ValueTask<ToolExecuteResult> ExecuteAsync(
            ExecuteInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            _ = cancellationToken;
            ObservedContext = context;

            var scope = context.Items is not null && context.Items.TryGetValue("scope", out var value)
                ? value as string
                : null;

            return ValueTask.FromResult(new ToolExecuteResult(ToolExecutionStatus.Success, $"{input.Text}|{scope}|{context.ExecutionSequence}"));
        }
    }

    private sealed class InvalidMethodToolTarget {
        [Tool("method.invalid_signature", "Invalid signature.")]
        public ValueTask<ToolExecuteResult> ExecuteAsync(
            ExecuteInput input,
            ExecuteInput anotherInput,
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            _ = context;
            _ = cancellationToken;
            return ValueTask.FromResult(new ToolExecuteResult(ToolExecutionStatus.Success, input.Text + anotherInput.Text));
        }
    }

    private sealed class ValidatedMethodToolTarget {
        public bool Invoked { get; private set; }

        [Tool("method.validation_failure", "Validate before invocation.")]
        public ValueTask<ToolExecuteResult> ExecuteAsync(
            ValidatedExecuteInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            _ = input;
            _ = context;
            _ = cancellationToken;
            Invoked = true;
            return ValueTask.FromResult(new ToolExecuteResult(ToolExecutionStatus.Success, "should not happen"));
        }
    }

    [Description("Input for method tool execution.")]
    private sealed record class ExecuteInput(
        [property: Description("Visible text.")]
        [property: JsonPropertyName("text")]
        string Text
    );

    [Description("Validated input for method tool execution.")]
    private sealed record class ValidatedExecuteInput(
        [property: Description("Visible text.")]
        [property: JsonPropertyName("text")]
        [property: MinLength(2)]
        string Text
    );
}
