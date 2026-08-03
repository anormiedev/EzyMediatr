using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EzyMediatr.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class EzyMediatrRegistrationGenerator : IIncrementalGenerator
{
    private const string GeneratedRegistrationType =
        "EzyMediatr.DependencyInjection.Generated.EzyMediatrGeneratedRegistration";

    private static readonly string[] HandlerInterfaceNames =
    {
        "EzyMediatr.Core.Handlers.IRequestHandler`2",
        "EzyMediatr.Core.Handlers.IStreamRequestHandler`2",
        "EzyMediatr.Core.Handlers.INotificationHandler`1",
        "EzyMediatr.Core.Pipeline.IRequestPreProcessor`1",
        "EzyMediatr.Core.Pipeline.IRequestPostProcessor`2",
        "EzyMediatr.Core.Pipeline.IPipelineBehavior`2",
        "EzyMediatr.Core.Pipeline.IStreamPipelineBehavior`2",
        "EzyMediatr.Core.Pipeline.INotificationPipelineBehavior`1"
    };

    private static readonly DiagnosticDescriptor InaccessibleType = new(
        "EZM001",
        "EzyMediatr source generation disabled",
        "Source-generated registration is disabled for this assembly because '{0}' is not accessible from generated code; runtime discovery will be used",
        "EzyMediatr",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax { BaseList: not null },
                static (syntaxContext, _) =>
                    syntaxContext.SemanticModel.GetDeclaredSymbol((TypeDeclarationSyntax)syntaxContext.Node) as INamedTypeSymbol)
            .Where(static symbol => symbol is not null)
            .Select(static (symbol, _) => symbol!);

        context.RegisterSourceOutput(
            context.CompilationProvider.Combine(candidates.Collect()),
            static (productionContext, input) => Generate(productionContext, input.Left, input.Right));
    }

    private static void Generate(
        SourceProductionContext context,
        Compilation compilation,
        ImmutableArray<INamedTypeSymbol> candidates)
    {
        if (compilation.GetTypeByMetadataName(GeneratedRegistrationType) is null)
        {
            return;
        }

        var handlerInterfaces = HandlerInterfaceNames
            .Select(compilation.GetTypeByMetadataName)
            .Where(static symbol => symbol is not null)
            .Select(static symbol => symbol!)
            .ToImmutableHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var validatorInterface = compilation.GetTypeByMetadataName("FluentValidation.IValidator`1");
        var registrations = new List<Registration>();
        var seenTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var type in candidates)
        {
            if (!seenTypes.Add(type) || !CanRegister(type))
            {
                continue;
            }

            foreach (var serviceType in type.AllInterfaces)
            {
                var definition = serviceType.OriginalDefinition;
                var isValidator = validatorInterface is not null &&
                    SymbolEqualityComparer.Default.Equals(definition, validatorInterface);
                if (!isValidator && !handlerInterfaces.Contains(definition))
                {
                    continue;
                }

                if (isValidator && !IsPubliclyVisible(type))
                {
                    continue;
                }

                if (!IsAccessibleFromGeneratedCode(type) || !IsAccessibleFromGeneratedCode(serviceType))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        InaccessibleType,
                        type.Locations.FirstOrDefault(),
                        type.ToDisplayString()));
                    context.AddSource(
                        "EzyMediatr.GeneratedRegistration.g.cs",
                        SourceText.From(RenderRuntimeFallback(), Encoding.UTF8));
                    return;
                }

                registrations.Add(new Registration(serviceType, type, isValidator));
            }
        }

        if (registrations.Count == 0)
        {
            return;
        }

        context.AddSource(
            "EzyMediatr.GeneratedRegistration.g.cs",
            SourceText.From(Render(registrations), Encoding.UTF8));
    }

    private static bool CanRegister(INamedTypeSymbol type)
    {
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || type.IsAbstract)
        {
            return false;
        }

        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.Arity != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPubliclyVisible(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAccessibleFromGeneratedCode(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol arrayType)
        {
            return IsAccessibleFromGeneratedCode(arrayType.ElementType);
        }

        if (type is not INamedTypeSymbol namedType || namedType.IsFileLocal)
        {
            return false;
        }

        for (var current = namedType; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (
                Accessibility.Public or
                Accessibility.Internal or
                Accessibility.ProtectedOrInternal))
            {
                return false;
            }
        }

        foreach (var typeArgument in namedType.TypeArguments)
        {
            if (!IsAccessibleFromGeneratedCode(typeArgument))
            {
                return false;
            }
        }

        return true;
    }

    private static string Render(IReadOnlyList<Registration> registrations)
    {
        var source = new StringBuilder(
            "// <auto-generated />\n" +
            "#nullable enable\n\n" +
            "namespace EzyMediatr.Generated;\n\n" +
            "[global::System.CodeDom.Compiler.GeneratedCode(\"EzyMediatr.Generators\", \"1.0.0\")]\n" +
            "internal static class GeneratedServiceRegistrar\n" +
            "{\n" +
            "    [global::System.Runtime.CompilerServices.ModuleInitializer]\n" +
            "    internal static void Initialize()\n" +
            "    {\n" +
            "        global::EzyMediatr.DependencyInjection.Generated.EzyMediatrGeneratedRegistration.Register(RegisterServices);\n" +
            "    }\n\n" +
            "    private static void RegisterServices(global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)\n" +
            "    {\n");
        var concreteValidators = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var registration in registrations)
        {
            var serviceType = registration.ServiceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var implementationType = registration.ImplementationType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            if (registration.IsValidator)
            {
                source.Append("        global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services, ")
                    .Append("global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Scoped(typeof(")
                    .Append(serviceType)
                    .Append("), typeof(")
                    .Append(implementationType)
                    .AppendLine(")));");
                concreteValidators.Add(registration.ImplementationType);
            }
            else
            {
                source.Append("        global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped(services, typeof(")
                    .Append(serviceType)
                    .Append("), typeof(")
                    .Append(implementationType)
                    .AppendLine("));");
            }
        }

        foreach (var validator in concreteValidators)
        {
            var validatorType = validator.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            source.Append("        global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAdd(services, ")
                .Append("global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Scoped(typeof(")
                .Append(validatorType)
                .Append("), typeof(")
                .Append(validatorType)
                .AppendLine(")));");
        }

        source.AppendLine("    }")
            .AppendLine("}");
        return source.ToString();
    }

    private static string RenderRuntimeFallback()
        => "// <auto-generated />\n" +
           "namespace EzyMediatr.Generated;\n\n" +
           "internal static class GeneratedServiceRegistrar\n" +
           "{\n" +
           "    [global::System.Runtime.CompilerServices.ModuleInitializer]\n" +
           "    internal static void Initialize()\n" +
           "    {\n" +
           "        global::EzyMediatr.DependencyInjection.Generated.EzyMediatrGeneratedRegistration.RequireRuntimeDiscovery();\n" +
           "    }\n" +
           "}\n";

    private readonly struct Registration
    {
        public Registration(INamedTypeSymbol serviceType, INamedTypeSymbol implementationType, bool isValidator)
        {
            ServiceType = serviceType;
            ImplementationType = implementationType;
            IsValidator = isValidator;
        }

        public INamedTypeSymbol ServiceType { get; }

        public INamedTypeSymbol ImplementationType { get; }

        public bool IsValidator { get; }
    }
}
