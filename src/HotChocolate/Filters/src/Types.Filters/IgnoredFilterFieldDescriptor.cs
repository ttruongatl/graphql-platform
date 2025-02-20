using System;
using System.Collections.Generic;
using System.Reflection;
using HotChocolate.Types.Descriptors;

namespace HotChocolate.Types.Filters;

[Obsolete("Use HotChocolate.Data.")]
internal class IgnoredFilterFieldDescriptor
    : FilterFieldDescriptorBase
{
    public IgnoredFilterFieldDescriptor(
        IDescriptorContext context,
        PropertyInfo property)
        : base(context, property)
    {
        Definition.Ignore = true;
    }

    protected override ISet<FilterOperationKind> AllowedOperations { get; } =
        new HashSet<FilterOperationKind>();

    protected override FilterOperationDefinition CreateOperationDefinition(
        FilterOperationKind operationKind)
    {
        throw new NotSupportedException();
    }
}
