using HotChocolate.Execution.Processing;
using HotChocolate.Fusion.Metadata;
using HotChocolate.Language;
using HotChocolate.Types;

namespace HotChocolate.Fusion.Planning;

/// <summary>
/// Represents the default execution step within the execution plan while
/// being in the planing phase.
/// </summary>
internal sealed class SelectionExecutionStep : ExecutionStep
{
    public SelectionExecutionStep(
        string subgraphName,
        IObjectType selectionSet,
        ObjectTypeMetadata selectionSetTypeMetadata)
        : this(subgraphName, null, selectionSet, selectionSetTypeMetadata)
    {
    }

    public SelectionExecutionStep(
        string subgraphName,
        ISelection? parentSelection,
        IObjectType selectionSet,
        ObjectTypeMetadata selectionSetTypeMetadata)
        : base(parentSelection, selectionSet, selectionSetTypeMetadata)
    {
        SubgraphName = subgraphName;
    }

    /// <summary>
    /// Gets the subgraph from which this execution step will fetch data.
    /// </summary>
    public string SubgraphName { get; }

    /// <summary>
    /// Gets the resolver for this execution step.
    /// </summary>
    public ResolverDefinition? Resolver { get; set; }

    /// <summary>
    /// Gets the root selections of this execution step.
    /// </summary>
    public List<RootSelection> RootSelections { get; } = new();

    /// <summary>
    /// Gets all selections that are part of this execution step.
    /// </summary>
    public HashSet<ISelection> AllSelections { get; } = new();

    /// <summary>
    /// Gets all selection sets that are part of this execution step.
    /// </summary>
    public HashSet<ISelectionSet> AllSelectionSets { get; } = new();

    /// <summary>
    /// Gets the selection resolvers.
    /// </summary>
    public Dictionary<ISelection, ResolverDefinition> SelectionResolvers { get; } = new();

    /// <summary>
    /// Gets a map for this execution task from the variable name
    /// to the internal state key.
    /// </summary>
    public Dictionary<string, string> Variables { get; } = new();

    /// <summary>
    /// Gets the argument target types.
    /// </summary>
    public Dictionary<string, ITypeNode> ArgumentTypes { get; } = new();

    /// <summary>
    /// The variable requirements by this task.
    /// </summary>
    public HashSet<string> Requires { get; } = new();
}
