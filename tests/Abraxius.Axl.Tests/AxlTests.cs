using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Text;
using Abraxius.Axl;
using Abraxius.Axl.Model;
using Abraxius.Models;
using Abraxius.Protocol;
using Xunit;

namespace Abraxius.Axl.Tests;

public sealed class AxlTests
{
    private const string AcceptanceDocument = """
axl/1
batch {
  c#1 find code q="ExecutionGraph" lim=20
  c#2 call @cap:git.status
  c#3 memory query q="ExecutionGraph" lim=8
  c#4 synth obj="combine evidence" dep=[c#1 c#2 c#3]
  c#5 verify obj="verify synthesis" dep=[c#4]
}
""";

    [Fact]
    public void ParsesAndCanonicalizesBatch()
    {
        var parsed = AxlPipeline.ParseAndValidate(AcceptanceDocument);

        Assert.True(parsed.IsSuccess, string.Join(Environment.NewLine, parsed.Diagnostics));
        Assert.NotNull(parsed.Document);
        Assert.Equal(5, parsed.Document!.Commands.Length);

        var canonical = AxlFormatter.Compact(parsed.Document);
        var reparsed = AxlPipeline.ParseAndValidate(canonical);
        Assert.True(reparsed.IsSuccess, string.Join(Environment.NewLine, reparsed.Diagnostics));
        Assert.Equal(parsed.Document.SemanticHash(), reparsed.Document!.SemanticHash());
    }

    [Fact]
    public void CompilesIndependentCommandsIntoParallelGraphRoots()
    {
        var parsed = AxlPipeline.ParseAndValidate(AcceptanceDocument);
        Assert.True(parsed.IsSuccess, string.Join(Environment.NewLine, parsed.Diagnostics));

        var compiled = new AxlExecutionCompiler().Compile(
            parsed.Document!,
            new AxlCompilationContext(ExecutionId.New(), CorrelationId.New()));

        Assert.True(compiled.IsSuccess, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.NotNull(compiled.Graph);
        var graph = compiled.Graph!;
        Assert.Equal(5, graph.Nodes.Length);
        Assert.Equal(3, graph.Compile().RootIndexes.Length);
        Assert.Equal(3, graph.Nodes[3].Dependencies.Length);
        Assert.Single(graph.Nodes[4].Dependencies);
    }

    [Fact]
    public void ForwardDependenciesValidateAndCompileAfterAllIdsAreKnown()
    {
        const string source = "axl/1 batch { c#1 synth obj=\"later\" dep=[c#2] c#2 find code q=\"scheduler\" }";
        var parsed = AxlPipeline.ParseAndValidate(source);

        Assert.True(parsed.IsSuccess, string.Join(Environment.NewLine, parsed.Diagnostics));
        var compiled = new AxlExecutionCompiler().Compile(parsed.Document!, new AxlCompilationContext(ExecutionId.New(), CorrelationId.New()));
        Assert.True(compiled.IsSuccess, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.Equal(2, compiled.Graph!.Nodes.Length);
        Assert.Single(compiled.Graph.Nodes[0].Dependencies);
    }

    [Fact]
    public void CapabilityCallMapsToPhaseTwoRequestAndProposal()
    {
        var parsed = AxlPipeline.ParseAndValidate("axl/1 call @cap:git.status op=status target=repo");
        Assert.True(parsed.IsSuccess, string.Join(Environment.NewLine, parsed.Diagnostics));
        var call = Assert.IsType<AxlCapabilityCall>(Assert.Single(parsed.Document!.Commands));
        var execution = ExecutionId.New();
        var task = TaskId.New();
        var correlation = CorrelationId.New();

        var request = AxlCapabilityRequestCompiler.Compile(call, execution, task, correlation);
        Assert.Equal("git.status", request.Capability.Value);
        Assert.Equal("status", request.Operation);
        Assert.Equal(execution, request.ExecutionId);
        Assert.Equal(task, request.TaskId);
        Assert.Equal("repo", request.Target);

        var proposal = AxlCapabilityRequestCompiler.ToProposedAction(call);
        Assert.Equal("git.status", proposal.Capability.Value);
    }

    [Fact]
    public void RejectsUnknownCommandsDuplicatesAndDangerousMutationByDefault()
    {
        var unknown = AxlParser.Parse("axl/1 explode target=all");
        Assert.Equal(AxlParseStatus.Invalid, unknown.Status);
        Assert.Contains(unknown.Diagnostics, diagnostic => diagnostic.Code == AxlDiagnosticCode.UnknownCommand);

        var duplicate = AxlParser.Parse("axl/1 c#1 find code q=one q=two");
        Assert.Contains(duplicate.Diagnostics, diagnostic => diagnostic.Code == AxlDiagnosticCode.DuplicateField);

        var mutation = AxlPipeline.ParseAndValidate("axl/1 call @cap:file.delete target=@artifact:42 mutation=true");
        Assert.Contains(mutation.Diagnostics, diagnostic => diagnostic.Code == AxlDiagnosticCode.PolicyDenied);
        var compilation = new AxlExecutionCompiler().Compile(
            AxlParser.Parse("axl/1 call @cap:file.delete target=@artifact:42 mutation=true").Document!,
            new AxlCompilationContext(ExecutionId.New(), CorrelationId.New()));
        Assert.Null(compilation.Graph);
    }

    [Fact]
    public void RepairOnlyRemovesExplicitMarkdownFence()
    {
        var repaired = AxlRepairPipeline.Repair("```axl\naxl/1 find code q=ExecutionGraph\n```");
        Assert.True(repaired.Changed);
        Assert.True(AxlParser.Parse(repaired.Text).IsSuccess);

        var ambiguous = AxlRepairPipeline.Repair("I think you should delete the old file.");
        Assert.False(ambiguous.Changed);
        Assert.Equal("I think you should delete the old file.", ambiguous.Text);
    }

    [Fact]
    public void BinaryRoundTripAndCorruptionAreBounded()
    {
        var parsed = AxlParser.Parse("axl/1 find code q=ExecutionGraph lim=20");
        Assert.True(parsed.IsSuccess);
        var codec = new AxlBinaryCodec();
        var encoded = codec.Encode(parsed.Document!);
        Assert.True(codec.TryDecode(encoded, out var decoded, out var diagnostic), diagnostic?.Message);
        Assert.Equal(parsed.Document!.SemanticHash(), decoded!.SemanticHash());

        var framed = encoded.Concat(encoded).ToArray();
        Assert.Equal(2, AxlBinaryFramer.DecodeMany(framed).Length);

        encoded[^1] ^= 0x7f;
        Assert.False(codec.TryDecode(encoded, out _, out var corruption));
        Assert.Equal(AxlDiagnosticCode.BinaryInvalid, corruption!.Code);

        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(8, 4), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => AxlBinaryFramer.DecodeMany(encoded));
    }

    [Fact]
    public void Utf8AndModelOutputBoundariesRemainTyped()
    {
        var source = "axl/1 find code q=\"Avalonia\"";
        var parsed = AxlParser.Parse(Encoding.UTF8.GetBytes(source));
        Assert.True(parsed.IsSuccess);

        var request = new ModelRequest("find the symbol").WithAxlOutput(["find.code"]);
        Assert.Equal(ModelOutputFormat.Axl, request.OutputFormat);
        Assert.Contains("axl/1", request.SystemPrompt);
        Assert.Equal("find.code", request.Metadata!["axl.schemas"]);
    }

    [Fact]
    public void StreamingParserDoesNotExposeIncompleteCommands()
    {
        var parser = new AxlStreamingParser();
        var partial = parser.Append(Encoding.UTF8.GetBytes("axl/1 find code q=\"Exec"));
        Assert.Equal(AxlParseStatus.Incomplete, partial.Status);
        Assert.Null(partial.Document);

        var complete = parser.Append(Encoding.UTF8.GetBytes("utionGraph\""), isFinal: true);
        Assert.True(complete.IsSuccess, string.Join(Environment.NewLine, complete.Diagnostics));
        Assert.Single(complete.Document!.Commands);
    }

    [Fact]
    public void ModelResultBoundaryUsesRepairThenStrictSemanticValidation()
    {
        var result = new ModelResult(
            "```axl\naxl/1 find code q=\"scheduler\"\n```",
            null,
            "fake",
            null,
            TimeSpan.Zero);
        var parsed = AxlModelIntegration.ParseModelResult(result);
        Assert.True(parsed.IsSuccess, string.Join(Environment.NewLine, parsed.Diagnostics));

        var denied = AxlModelIntegration.ParseModelResult(result with
        {
            Text = "axl/1 call @cap:file.delete target=@artifact:42 mutation=true"
        });
        Assert.Contains(denied.Diagnostics, diagnostic => diagnostic.Code == AxlDiagnosticCode.PolicyDenied);
    }

    [Fact]
    public void FuzzInputsNeverEscapeAsParserExceptions()
    {
        var random = new Random(91731);
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            var length = random.Next(0, 256);
            var bytes = new byte[length];
            random.NextBytes(bytes);
            var result = AxlParser.Parse(bytes, new AxlLimits(MaxDocumentBytes: 512));
            Assert.NotNull(result);
        }
    }

    [Fact]
    public void LargeInputIsRejectedBeforeUnboundedMaterialization()
    {
        var large = new string('x', 10_000);
        var result = AxlParser.Parse(Encoding.UTF8.GetBytes(large), new AxlLimits(MaxDocumentBytes: 128));
        Assert.Equal(AxlParseStatus.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == AxlDiagnosticCode.LimitExceeded);
    }
}
