using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HotChocolate.Fusion.Clients;
using HotChocolate.Language;
using static HotChocolate.Fusion.Execution.ExecutorUtils;

namespace HotChocolate.Fusion.Execution.Nodes;

/// <summary>
/// The resolver node is responsible for batch fetching data from a subgraph.
/// </summary>
internal sealed class ResolveByKeyBatch : ResolverNodeBase
{
    private readonly Dictionary<string, ITypeNode> _argumentTypes;

    /// <summary>
    /// Initializes a new instance of <see cref="ResolveByKeyBatch"/>.
    /// </summary>
    /// <param name="id">
    /// The unique id of this node.
    /// </param>
    /// <param name="config">
    /// Gets the resolver configuration.
    /// </param>
    /// <param name="argumentTypes">
    /// The argument types that are required to build the batch request.
    /// </param>
    public ResolveByKeyBatch(int id, Config config, IReadOnlyDictionary<string, ITypeNode> argumentTypes)
        : base(id, config)
    {
        _argumentTypes = new Dictionary<string, ITypeNode>(argumentTypes, StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the kind of this node.
    /// </summary>
    public override QueryPlanNodeKind Kind => QueryPlanNodeKind.ResolveByKeyBatch;
    
    /// <summary>
    /// Executes this resolver node.
    /// </summary>
    /// <param name="context">
    /// The execution context.
    /// </param>
    /// <param name="state">
    /// The execution state.
    /// </param>
    /// <param name="cancellationToken">
    /// The cancellation token.
    /// </param>
    protected override async Task OnExecuteAsync(
        FusionExecutionContext context,
        RequestState state,
        CancellationToken cancellationToken)
    {
        if (state.TryGetState(SelectionSet, out var executionState))
        {
            InitializeRequests(context, executionState);

            var batchExecutionState = CreateBatchBatchState(executionState, Requires);

            // Create the batch subgraph request.
            var variableValues = BuildVariables(batchExecutionState, _argumentTypes);
            var request = CreateRequest(context.OperationContext.Variables, variableValues);

            // Once we have the batch request, we will enqueue it for execution with
            // the execution engine.
            var response = await context.ExecuteAsync(SubgraphName, request, cancellationToken).ConfigureAwait(false);

            // Before we extract the data from the responses we will enqueue the responses
            // for cleanup so that the memory can be released at the end of the execution.
            context.Result.RegisterForCleanup(response, ReturnResult);

            ProcessResult(context, response, batchExecutionState);
        }
    }
    
    /// <summary>
    /// Executes this resolver node.
    /// </summary>
    /// <param name="context">
    /// The execution context.
    /// </param>
    /// <param name="state">
    /// The execution state.
    /// </param>
    /// <param name="cancellationToken">
    /// The cancellation token.
    /// </param>
    protected override async Task OnExecuteNodesAsync(
        FusionExecutionContext context,
        RequestState state,
        CancellationToken cancellationToken)
    {
        if (state.ContainsState(SelectionSet))
        {
            await base.OnExecuteNodesAsync(context, state, cancellationToken).ConfigureAwait(false);
        }
    }
    
    private static void InitializeRequests(FusionExecutionContext context, List<ExecutionState> executionState)
    {
        ref var state = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(executionState));
        ref var end = ref Unsafe.Add(ref state, executionState.Count);            
        
        while (Unsafe.IsAddressLessThan(ref state, ref end))
        {
            TryInitializeExecutionState(context.QueryPlan, state);
            state = ref Unsafe.Add(ref state, 1)!;
        }
    }

    private void ProcessResult(
        FusionExecutionContext context, 
        GraphQLResponse response, 
        BatchExecutionState[] batchExecutionState)
    {
        ExtractErrors(context.Result, response.Errors, context.ShowDebugInfo);
        var result = UnwrapResult(response, Requires);
        
        ref var batchState = ref MemoryMarshal.GetArrayDataReference(batchExecutionState);
        ref var end = ref Unsafe.Add(ref batchState, batchExecutionState.Length);

        while (Unsafe.IsAddressLessThan(ref batchState, ref end))
        {
            if (result.TryGetValue(batchState.Key, out var data))
            {
                ExtractSelectionResults(SelectionSet, SubgraphName, data, batchState.SelectionResults);
                ExtractVariables(data, context.QueryPlan, SelectionSet, batchState.Provides, batchState.VariableValues);
            }

            batchState = ref Unsafe.Add(ref batchState, 1);
        }
    }

    private static Dictionary<string, IValueNode> BuildVariables(
        BatchExecutionState[] batchExecutionState,
        Dictionary<string, ITypeNode> argumentTypes)
    {
        var first = batchExecutionState[0];
        
        if (batchExecutionState.Length == 1)
        {
            return first.VariableValues;
        }

        var variableValues = new Dictionary<string, IValueNode>();
        var uniqueState = new List<BatchExecutionState>();
        var processed = new HashSet<string>();

        ref var batchState = ref MemoryMarshal.GetArrayDataReference(batchExecutionState);
        ref var end = ref Unsafe.Add(ref batchState, batchExecutionState.Length);

        while (Unsafe.IsAddressLessThan(ref batchState, ref end))
        {
            if (processed.Add(batchState.Key))
            {
                uniqueState.Add(batchState);
            }

            batchState = ref Unsafe.Add(ref batchState, 1);
        }

        foreach (var key in first.VariableValues.Keys)
        {
            var expectedType = argumentTypes[key];

            if (expectedType.IsListType())
            {
                var list = new List<IValueNode>();

                foreach (var value in uniqueState)
                {
                    if (value.VariableValues.TryGetValue(key, out var variableValue))
                    {
                        list.Add(variableValue);
                    }
                }

                variableValues.Add(key, new ListValueNode(list));
            }
            else
            {
                if (batchExecutionState[0].VariableValues.TryGetValue(key, out var variableValue))
                {
                    variableValues.Add(key, variableValue);
                }
            }
        }

        return variableValues;
    }

    private Dictionary<string, JsonElement> UnwrapResult(
        GraphQLResponse response,
        IReadOnlyList<string> exportKeys)
    {
        JsonElement data = response.Data;
        var path = Path;

        if (data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return new Dictionary<string, JsonElement>();
        }

        if (path.Length > 0)
        {
            data = LiftData(data, Path);
        }

        if (data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return new Dictionary<string, JsonElement>();
        }

        var result = new Dictionary<string, JsonElement>();

        if (exportKeys.Count == 1)
        {
            var key = exportKeys[0];
            foreach (var element in data.EnumerateArray())
            {
                if (element.TryGetProperty(key, out var keyValue))
                {
                    result.TryAdd(FormatKeyValue(keyValue), element);
                }
            }
        }
        else
        {
            foreach (var element in data.EnumerateArray())
            {
                var key = string.Empty;

                foreach (var exportKey in exportKeys)
                {
                    if (element.TryGetProperty(exportKey, out var keyValue))
                    {
                        key += FormatKeyValue(keyValue);
                    }
                }

                result.TryAdd(key, element);
            }
        }

        return result;
    }
    
    private static JsonElement LiftData(JsonElement data, string[] path)
    {
        if (data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return data;
        }
        
        var current = data;
            
        ref var segment = ref MemoryMarshal.GetArrayDataReference(path);
        ref var end = ref Unsafe.Add(ref segment, path.Length);

        while(Unsafe.IsAddressLessThan(ref segment, ref end))
        {
            if (current.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                return current;
            }

            current.TryGetProperty(segment, out var propertyValue);
            current = propertyValue;
                
            segment = ref Unsafe.Add(ref segment, 1)!;
        }

        return current;
    }
    
    private static BatchExecutionState[] CreateBatchBatchState(List<ExecutionState> executionState, string[] requires)
    {
        var batchExecutionState = new BatchExecutionState[executionState.Count];
        
        ref var state = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(executionState));
        ref var batchState = ref MemoryMarshal.GetArrayDataReference(batchExecutionState);
        ref var end = ref Unsafe.Add(ref state, executionState.Count);

        if (requires.Length == 1)
        {
            while (Unsafe.IsAddressLessThan(ref state, ref end))
            {
                var key = FormatKeyValue(state.VariableValues[requires[0]]);
                batchState = new BatchExecutionState(key, state);

                state = ref Unsafe.Add(ref state, 1)!;
                batchState = ref Unsafe.Add(ref batchState, 1)!;
            }
        }
        else
        {
            var keyBuilder = new StringBuilder();
            
            while (Unsafe.IsAddressLessThan(ref state, ref end))
            {
                ref var key = ref MemoryMarshal.GetArrayDataReference(requires);
                ref var keyEnd = ref Unsafe.Add(ref key, requires.Length);

                while (Unsafe.IsAddressLessThan(ref key, ref keyEnd))
                {
                    keyBuilder.Append(FormatKeyValue(state.VariableValues[key]));
                    key = ref Unsafe.Add(ref key, 1)!;
                }

                batchState = new BatchExecutionState(key, state);

                state = ref Unsafe.Add(ref state, 1)!;
                batchState = ref Unsafe.Add(ref batchState, 1)!;
            }
        }

        return batchExecutionState;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string FormatKeyValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => throw new NotSupportedException(),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string FormatKeyValue(IValueNode element)
        => element switch
        {
            StringValueNode value => value.Value,
            IntValueNode value => value.ToString(),
            FloatValueNode value => value.ToString(),
            BooleanValueNode { Value: true } => "true",
            BooleanValueNode { Value: false } => "false",
            NullValueNode => "null",
            _ => throw new NotSupportedException(),
        };

    private readonly struct BatchExecutionState
    {
        public BatchExecutionState(
            string batchKey,
            ExecutionState executionState)
        {
            Key = batchKey;
            VariableValues = executionState.VariableValues;
            Provides = executionState.Provides;
            SelectionResults = executionState.SelectionSetData;
        }

        public string Key { get; }

        public Dictionary<string, IValueNode> VariableValues { get; }

        /// <summary>
        /// Gets a list of keys representing the state that is being
        /// provided after the associated <see cref="ResolverNodeBase.SelectionSet"/>
        /// has been executed.
        /// </summary>
        public IReadOnlyList<string> Provides { get; }

        public SelectionData[] SelectionResults { get; }
    }
}
