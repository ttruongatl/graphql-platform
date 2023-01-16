using System.Text;
using HotChocolate.Types.Analyzers.Inspectors;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using static System.StringComparison;
using static HotChocolate.Types.Analyzers.Properties.SourceGenResources;
using static HotChocolate.Types.Analyzers.StringConstants;
using static HotChocolate.Types.Analyzers.WellKnownTypes;

namespace HotChocolate.Types.Analyzers.Generators;

public class DataLoaderGenerator : ISyntaxGenerator
{
    private static readonly DiagnosticDescriptor _keyParameterMissing =
        new(
            id: "HC0074",
            title: "Parameter Missing.",
            messageFormat:
            DataLoader_KeyParameterMissing,
            category: "DataLoader",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor _methodAccessModifierInvalid =
        new(
            id: "HC0075",
            title: "Access Modifier Invalid.",
            messageFormat:
            DataLoader_InvalidAccessModifier,
            category: "DataLoader",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorPostInitializationContext context)
    {
    }

    public bool Consume(ISyntaxInfo syntaxInfo)
        => syntaxInfo is DataLoaderInfo or ModuleInfo or DataLoaderDefaultsInfo;

    public void Generate(
        SourceProductionContext context,
        Compilation compilation,
        IReadOnlyCollection<ISyntaxInfo> syntaxInfos)
    {
        var module =
            syntaxInfos.OfType<ModuleInfo>().FirstOrDefault() ??
            new ModuleInfo(
                compilation.AssemblyName is null
                    ? "AssemblyTypes"
                    : compilation.AssemblyName?.Split('.').Last() + "Types",
                ModuleOptions.Default);

        var defaults =
            syntaxInfos.OfType<DataLoaderDefaultsInfo>().FirstOrDefault() ??
            new DataLoaderDefaultsInfo(null, null, true, true);

        var processed = new HashSet<string>(StringComparer.Ordinal);
        var dataLoaders = new List<DataLoaderInfo>();
        var sourceText = new StringBuilder();

        sourceText.AppendLine("// <auto-generated/>");
        sourceText.AppendLine("#nullable enable");
        sourceText.AppendLine("using System;");
        sourceText.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sourceText.AppendLine("using HotChocolate.Execution.Configuration;");

        foreach (var syntaxInfo in syntaxInfos)
        {
            if (syntaxInfo is DataLoaderInfo dataLoader)
            {
                if (dataLoader.MethodSymbol.Parameters.Length == 0)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            _keyParameterMissing,
                            Location.Create(
                                dataLoader.MethodSyntax.SyntaxTree,
                                dataLoader.MethodSyntax.ParameterList.Span)));
                    continue;
                }

                if (dataLoader.MethodSymbol.DeclaredAccessibility is not Accessibility.Public
                    and not Accessibility.Internal and not Accessibility.ProtectedAndInternal)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            _methodAccessModifierInvalid,
                            Location.Create(
                                dataLoader.MethodSyntax.SyntaxTree,
                                dataLoader.MethodSyntax.Modifiers.Span)));
                    continue;
                }

                var keyArg = dataLoader.MethodSymbol.Parameters[0];
                var keyType = keyArg.Type;
                var cancellationTokenIndex = -1;
                var serviceMap = new Dictionary<int, string>();

                if (IsKeysArgument(keyType))
                {
                    keyType = ExtractKeyType(keyType);
                }

                InspectDataLoaderParameters(
                    dataLoader,
                    ref cancellationTokenIndex,
                    serviceMap);

                DataLoaderKind kind;

                if (IsReturnTypeDictionary(dataLoader.MethodSymbol.ReturnType, keyType))
                {
                    kind = DataLoaderKind.Batch;
                }
                else if (IsReturnTypeLookup(dataLoader.MethodSymbol.ReturnType, keyType))
                {
                    kind = DataLoaderKind.Group;
                }
                else
                {
                    kind = DataLoaderKind.Cache;
                }

                var valueType = ExtractValueType(dataLoader.MethodSymbol.ReturnType, kind);

                if (processed.Add(dataLoader.FullName))
                {
                    dataLoaders.Add(dataLoader);

                    GenerateDataLoader(
                        dataLoader,
                        defaults,
                        kind,
                        keyType,
                        valueType,
                        dataLoader.MethodSymbol.Parameters.Length,
                        cancellationTokenIndex,
                        serviceMap,
                        sourceText);
                }
            }
        }

        // if we find no valid DataLoader we will not create any file.
        if (dataLoaders.Count > 0)
        {
            if (defaults.RegisterServices)
            {
                // write DI integration
                sourceText.AppendLine();
                sourceText.AppendLine("namespace Microsoft.Extensions.DependencyInjection");
                sourceText.AppendLine("{");
                GenerateDataLoaderRegistrations(module, dataLoaders, sourceText);
                sourceText.AppendLine("}");
            }

            context.AddSource(
                WellKnownFileNames.DataLoaderFile,
                SourceText.From(sourceText.ToString(), Encoding.UTF8));
        }
    }

    private static void GenerateDataLoader(
        DataLoaderInfo dataLoader,
        DataLoaderDefaultsInfo defaults,
        DataLoaderKind kind,
        ITypeSymbol keyType,
        ITypeSymbol valueType,
        int parameterCount,
        int cancelIndex,
        Dictionary<int, string> services,
        StringBuilder sourceText)
    {
        var isScoped =  dataLoader.IsScoped ?? defaults.Scoped ?? false;
        var isPublic = dataLoader.IsPublic ?? defaults.IsPublic ?? true;
        var isInterfacePublic = dataLoader.IsInterfacePublic ?? defaults.IsInterfacePublic ?? true;

        sourceText.AppendLine();
        sourceText.Append("namespace ");
        sourceText.AppendLine(dataLoader.Namespace);
        sourceText.AppendLine("{");

        // first we generate a DataLoader interface ...
        var interfaceName = dataLoader.InterfaceName;

        if (isInterfacePublic)
        {
            sourceText.Append("    public interface ");
        }
        else
        {
            sourceText.Append("    internal interface ");
        }

        sourceText.Append(interfaceName);

        if (kind is DataLoaderKind.Batch or DataLoaderKind.Cache)
        {
            sourceText.Append(" : global::GreenDonut.IDataLoader<");
            sourceText.Append("global::");
            sourceText.Append(ToTypeName(keyType));
            sourceText.Append(", ");
            sourceText.Append("global::");
            sourceText.Append(ToTypeName(valueType));
            sourceText.Append(">");
        }
        else if (kind is DataLoaderKind.Group)
        {
            sourceText.Append(" : global::GreenDonut.IDataLoader<");
            sourceText.Append("global::");
            sourceText.Append(ToTypeName(keyType));
            sourceText.Append(", ");
            sourceText.Append("global::");
            sourceText.Append(ToTypeName(valueType));
            sourceText.Append("[]>");
        }

        sourceText.AppendLine(" { }");
        sourceText.AppendLine();

        // ... then the actual DataLoader implementation.
        if (isPublic)
        {
            sourceText.Append("    public sealed class ");
        }
        else
        {
            sourceText.Append("    internal sealed class ");
        }

        sourceText.Append(dataLoader.Name);

        if (kind is DataLoaderKind.Batch)
        {
            sourceText.Append(" : global::GreenDonut.BatchDataLoader<");
            sourceText.Append("global::");
            sourceText.Append(ToTypeName(keyType));
            sourceText.Append(", ");
            sourceText.Append("global::");
            sourceText.Append(ToTypeName(valueType));
            sourceText.Append(">");
        }
        else if (kind is DataLoaderKind.Group)
        {
            sourceText.Append(" : global::GreenDonut.GroupedDataLoader<");
            sourceText.Append("global::");
            sourceText.Append(ToTypeName(keyType));
            sourceText.Append(", ");
            sourceText.Append("global::");
            sourceText.Append(ToTypeName(valueType));
            sourceText.Append(">");
        }
        else if (kind is DataLoaderKind.Cache)
        {
            sourceText.Append(" : global::GreenDonut.CacheDataLoader<");
            sourceText.Append("global::");
            sourceText.Append(ToTypeName(keyType));
            sourceText.Append(", ");
            sourceText.Append("global::");
            sourceText.Append(ToTypeName(valueType));
            sourceText.Append(">");
        }

        sourceText.Append(", ");
        sourceText.AppendLine(interfaceName);
        sourceText.AppendLine("    {");

        sourceText.AppendLine("        private readonly IServiceProvider _services;");
        sourceText.AppendLine();

        if (kind is DataLoaderKind.Batch or DataLoaderKind.Group)
        {
            sourceText
                .Append(Indent)
                .Append(Indent)
                .AppendLine($"public {dataLoader.Name}(");
            sourceText
                .Append(Indent)
                .Append(Indent)
                .Append(Indent)
                .AppendLine("IServiceProvider services,");
            sourceText
                .Append(Indent)
                .Append(Indent)
                .Append(Indent)
                .AppendLine("global::GreenDonut.IBatchScheduler batchScheduler,");
            sourceText
                .Append(Indent)
                .Append(Indent)
                .Append(Indent)
                .AppendLine("global::GreenDonut.DataLoaderOptions? options = null)");

            sourceText
                .Append(Indent)
                .Append(Indent)
                .Append(Indent)
                .AppendLine(": base(batchScheduler, options)");
        }
        else
        {
            sourceText
                .Append(Indent)
                .Append(Indent)
                .AppendLine($"public {dataLoader.Name}(");
            sourceText
                .Append(Indent)
                .Append(Indent)
                .Append(Indent)
                .AppendLine("IServiceProvider services,");
            sourceText
                .Append(Indent)
                .Append(Indent)
                .Append(Indent)
                .AppendLine("global::GreenDonut.DataLoaderOptions? options = null)");

            sourceText
                .Append(Indent)
                .Append(Indent)
                .Append(Indent)
                .AppendLine(": base(options)");
        }


        sourceText.AppendLine("        {");
        sourceText.AppendLine("            _services = services ??");
        sourceText.AppendLine("                throw new ArgumentNullException(nameof(services));");
        sourceText.AppendLine("        }");
        sourceText.AppendLine();

        if (kind is DataLoaderKind.Batch)
        {
            sourceText.Append($"        protected override async {WellKnownTypes.Task}<");
            sourceText.Append($"global::{ReadOnlyDictionary}<");
            sourceText.Append("global::");
            sourceText.Append(ToTypeName(keyType));
            sourceText.Append(", global::");
            sourceText.Append(ToTypeName(valueType));
            sourceText.Append(">> ");
            sourceText.AppendLine("LoadBatchAsync(");
            sourceText.Append($"            global::{ReadOnlyList}<");
            sourceText.AppendLine($"global::{ToTypeName(keyType)}> keys,");
            sourceText.AppendLine($"            global::{WellKnownTypes.CancellationToken} ct)");
        }
        else if (kind is DataLoaderKind.Group)
        {
            sourceText.Append($"        protected override async {WellKnownTypes.Task}<");
            sourceText.Append($"global::{Lookup}<");
            sourceText.Append("global::");
            sourceText.Append(ToTypeName(keyType));
            sourceText.Append(", global::");
            sourceText.Append(ToTypeName(valueType));
            sourceText.Append(">> ");
            sourceText.AppendLine("LoadGroupedBatchAsync(");
            sourceText.Append($"            global::{ReadOnlyList}<");
            sourceText.AppendLine($"global::{ToTypeName(keyType)}> keys,");
            sourceText.AppendLine($"            global::{WellKnownTypes.CancellationToken} ct)");
        }
        else if (kind is DataLoaderKind.Cache)
        {
            sourceText.Append($"        protected override async {WellKnownTypes.Task}<");
            sourceText.Append("global::");
            sourceText.Append(ToTypeName(valueType));
            sourceText.Append("> ");
            sourceText.AppendLine("LoadSingleAsync(");
            sourceText.AppendLine($"            global::{ToTypeName(keyType)} key,");
            sourceText.AppendLine($"            global::{WellKnownTypes.CancellationToken} ct)");
        }

        sourceText.AppendLine("        {");

        if (isScoped)
        {
            sourceText
                .Append(Indent)
                .Append(Indent)
                .Append(Indent)
                .AppendLine("await using var scope = _services.CreateAsyncScope();");

            foreach (var item in services.OrderBy(t => t.Key))
            {
                sourceText.Append($"            var p{item.Key} = ");
                sourceText.Append("scope.ServiceProvider.GetRequiredService<");
                sourceText.Append(item.Value);
                sourceText.AppendLine(">();");
            }
        }
        else
        {
            foreach (var item in services.OrderBy(t => t.Key))
            {
                sourceText.Append($"            var p{item.Key} = _services.GetRequiredService<");
                sourceText.Append(item.Value);
                sourceText.AppendLine(">();");
            }
        }

        sourceText.Append("            return await global::");
        sourceText.Append(dataLoader.ContainingType);
        sourceText.Append(".");
        sourceText.Append(dataLoader.MethodName);
        sourceText.Append("(");

        for (var i = 0; i < parameterCount; i++)
        {
            if (i > 0)
            {
                sourceText.Append(", ");
            }

            if (i == 0)
            {
                if (kind is DataLoaderKind.Batch or DataLoaderKind.Group)
                {
                    sourceText.Append("keys");
                }
                else
                {
                    sourceText.Append("key");
                }
            }
            else if (i == cancelIndex)
            {
                sourceText.Append("ct");
            }
            else
            {
                sourceText.Append("p");
                sourceText.Append(i);
            }
        }
        sourceText.AppendLine(").ConfigureAwait(false);");

        sourceText.AppendLine("        }");
        sourceText.AppendLine("    }");
        sourceText.AppendLine("}");
    }

    private static void GenerateDataLoaderRegistrations(
        ModuleInfo module,
        List<DataLoaderInfo> dataLoaders,
        StringBuilder sourceText)
    {
        sourceText.Append(Indent)
            .Append("public static partial class ")
            .Append(module.ModuleName)
            .AppendLine("RequestExecutorBuilderExtensions");

        sourceText
            .Append(Indent)
            .AppendLine("{");

        sourceText
            .Append(Indent)
            .Append(Indent)
            .Append("static partial void RegisterGeneratedDataLoader(")
            .AppendLine("IRequestExecutorBuilder builder)");

        sourceText
            .Append(Indent)
            .Append(Indent)
            .AppendLine("{");

        foreach (var dataLoader in dataLoaders)
        {
            sourceText
                .Append(Indent)
                .Append(Indent)
                .Append(Indent)
                .Append("builder.AddDataLoader<")
                .Append("global::")
                .Append(dataLoader.InterfaceFullName)
                .Append(", global::")
                .Append(dataLoader.FullName)
                .AppendLine(">();");
        }

        sourceText
            .Append(Indent)
            .Append(Indent)
            .AppendLine("}");

        sourceText
            .Append(Indent)
            .AppendLine("}");
    }

    private void InspectDataLoaderParameters(
        DataLoaderInfo dataLoader,
        ref int cancellationTokenIndex,
        Dictionary<int, string> serviceMap)
    {
        for (var i = 1; i < dataLoader.MethodSymbol.Parameters.Length; i++)
        {
            var argument = dataLoader.MethodSymbol.Parameters[i];
            var argumentType = ToTypeName(argument.Type);

            if (IsCancellationToken(argumentType))
            {
                if (cancellationTokenIndex != -1)
                {
                    // report error
                    return;
                }

                cancellationTokenIndex = i;
            }
            else
            {
                serviceMap[i] = argumentType;
            }
        }
    }

    private static bool IsKeysArgument(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } namedType &&
            ReadOnlyList.Equals(ToTypeName(namedType), Ordinal))
        {
            return true;
        }

        return false;
    }

    private static ITypeSymbol ExtractKeyType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } namedType &&
            ReadOnlyList.Equals(ToTypeName(namedType), Ordinal))
        {
            return namedType.TypeArguments[0];
        }

        throw new InvalidOperationException();
    }

    private static bool IsCancellationToken(string typeName)
        => string.Equals(typeName, WellKnownTypes.CancellationToken);

    private static string ToTypeName(ITypeSymbol type)
        => $"{type.ContainingNamespace}.{type.Name}";

    private static bool IsReturnTypeDictionary(ITypeSymbol returnType, ITypeSymbol keyType)
    {
        if (returnType is INamedTypeSymbol { TypeArguments.Length: 1 } namedType)
        {
            var resultType = namedType.TypeArguments[0];

            if (IsReadOnlyDictionary(resultType) &&
                resultType is INamedTypeSymbol { TypeArguments.Length: 2 } dictionaryType &&
                dictionaryType.TypeArguments[0].Equals(keyType, SymbolEqualityComparer.Default))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsReturnTypeLookup(ITypeSymbol returnType, ITypeSymbol keyType)
    {
        if (returnType is INamedTypeSymbol { TypeArguments.Length: 1 } namedType)
        {
            var resultType = namedType.TypeArguments[0];

            if (ToTypeName(resultType).Equals(Lookup, Ordinal) &&
                resultType is INamedTypeSymbol { TypeArguments.Length: 2 } dictionaryType &&
                dictionaryType.TypeArguments[0].Equals(keyType, SymbolEqualityComparer.Default))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsReadOnlyDictionary(ITypeSymbol type)
    {
        if (!ToTypeName(type).Equals(ReadOnlyDictionary, Ordinal))
        {
            foreach (var interfaceSymbol in type.Interfaces)
            {
                if (ToTypeName(interfaceSymbol).Equals(ReadOnlyDictionary, Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        return true;
    }

    private static ITypeSymbol ExtractValueType(ITypeSymbol returnType, DataLoaderKind kind)
    {
        if (returnType is INamedTypeSymbol { TypeArguments.Length: 1 } namedType)
        {
            if (kind is DataLoaderKind.Batch or DataLoaderKind.Group &&
                namedType.TypeArguments[0] is INamedTypeSymbol { TypeArguments.Length: 2 } dict)
            {
                return dict.TypeArguments[1];
            }

            if (kind is DataLoaderKind.Cache)
            {
                return namedType.TypeArguments[0];
            }
        }

        throw new InvalidOperationException();
    }
}
