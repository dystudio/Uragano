﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyModel;
using Uragano.Abstractions;

namespace Uragano.DynamicProxy
{
    public class ProxyGenerator
    {
        public static List<Type> GenerateProxy(List<Type> interfaces)
        {
            if (interfaces.Any(p => !p.IsInterface && !typeof(IService).IsAssignableFrom(p)))
                throw new ArgumentException("The proxy object must be an interface and inherit IService.", nameof(interfaces));

            var assemblies = DependencyContext.Default.RuntimeLibraries.SelectMany(i => i.GetDefaultAssemblyNames(DependencyContext.Default).Select(z => Assembly.Load(new AssemblyName(z.Name)))).Where(i => !i.IsDynamic);

            var types = assemblies.Select(p => p.GetType());
            types = types.Except(interfaces);
            foreach (var type in types)
            {
                assemblies = assemblies.Append(type.Assembly);
            }

            var trees = interfaces.Select(GenerateProxyTree).ToList();
            using (var stream = CompileClientProxy(trees,
                assemblies.Select(x => MetadataReference.CreateFromFile(x.Location))
                    .Concat(new[]
                    {
                        MetadataReference.CreateFromFile(typeof(Task).GetTypeInfo().Assembly.Location)
                    })))
            {
                var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
                return assembly.GetExportedTypes().ToList();
            }
        }


        private static SyntaxTree GenerateProxyTree(Type @interface)
        {
            var syntax = SyntaxFactory.CompilationUnit()
                .WithUsings(SyntaxFactory.List(new[]
                {
                    SyntaxFactory.UsingDirective(SyntaxFactory.IdentifierName("System")),
                    SyntaxFactory.UsingDirective(GenerateQualifiedNameSyntax("System.Threading.Tasks")),
                    SyntaxFactory.UsingDirective(GenerateQualifiedNameSyntax("System.Collections.Generic")),
                    SyntaxFactory.UsingDirective(GenerateQualifiedNameSyntax(typeof(DynamicProxyAbstract).Namespace)),
                    SyntaxFactory.UsingDirective(GenerateQualifiedNameSyntax(typeof(IRemotingInvoke).Namespace)),
                    SyntaxFactory.UsingDirective(GenerateQualifiedNameSyntax(@interface.Namespace))
                }))
                .WithMembers(GenerateNamespace(@interface));

            return syntax.NormalizeWhitespace().SyntaxTree;
        }

        /// <summary>
        /// Generate namespace
        /// </summary>
        /// <returns></returns>
        private static SyntaxList<MemberDeclarationSyntax> GenerateNamespace(Type type)
        {
            var serviceNameAttr = type.GetCustomAttribute<ServiceDiscoveryNameAttribute>();
            var namespaceDeclaration = SyntaxFactory.NamespaceDeclaration(GenerateQualifiedNameSyntax("Uragano.DynamicProxy.UraganoProxy"));
            return GenerateClass(namespaceDeclaration, type, serviceNameAttr.Name);
        }

        private static SyntaxList<MemberDeclarationSyntax> GenerateClass(NamespaceDeclarationSyntax namespaceDeclaration, Type type, string serviceName)
        {
            var className = type.Name.TrimStart('I') + "_____UraganoClientProxy";

            //var serviceProviderProperty = SyntaxFactory.PropertyDeclaration(SyntaxFactory.ParseTypeName("IServiceProvider"), " ServiceProvider")
            //	.AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword))
            //	.AddAccessorListAccessors(
            //		SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));

            var classDeclaration = SyntaxFactory.ClassDeclaration(className)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                .AddBaseListTypes(
                    SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName("DynamicProxyAbstract")),
                    SyntaxFactory.SimpleBaseType(GenerateQualifiedNameSyntax(type)))
                //.AddMembers(serviceProviderProperty)
                .AddMembers(GenerateMethods(type, className, serviceName));
            return SyntaxFactory.SingletonList<MemberDeclarationSyntax>(namespaceDeclaration.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(classDeclaration)));
        }

        private static MemberDeclarationSyntax[] GenerateMethods(Type type, string className, string serviceName)
        {
            var typeAttr = type.GetCustomAttribute<ServiceRouteAttribute>();
            var routePrefix = typeAttr == null ? $"{type.Namespace}/{type.Name}" : typeAttr.Route;
            var methods = type.GetMethods().ToList();

            var s = methods.Select(p => GenerateMethod(routePrefix, p, serviceName)).ToList();
            s.Insert(0, GenerateConstructorDeclaration(className));
            return s.ToArray();
        }

        private static MemberDeclarationSyntax GenerateMethod(string routePrefix, MethodInfo methodInfo, string serviceName)
        {
            if (methodInfo.ReturnType.Namespace != typeof(Task).Namespace)
                throw new NotSupportedException($"Only support proxy asynchronous methods.[{methodInfo.DeclaringType?.Namespace}.{methodInfo.DeclaringType?.Name}.{methodInfo.Name}]");

            var methodAttr = methodInfo.GetCustomAttribute<ServiceRouteAttribute>();
            var serviceRoute = $"{routePrefix}/{(methodAttr == null ? methodInfo.Name : methodAttr.Route)}";
            var returnDeclaration = GenerateType(methodInfo.ReturnType);

            var argDeclarations = new List<SyntaxNodeOrToken>();
            foreach (var arg in methodInfo.GetParameters())
            {
                argDeclarations.Add(arg.ParameterType.IsGenericType
                    ? SyntaxFactory.Parameter(SyntaxFactory.Identifier(arg.Name))
                        .WithType(GenerateType(arg.ParameterType))
                    : SyntaxFactory.Parameter(SyntaxFactory.Identifier(arg.Name))
                        .WithType(GenerateQualifiedNameSyntax(arg.ParameterType)));

                argDeclarations.Add(SyntaxFactory.Token(SyntaxKind.CommaToken));
            }

            if (argDeclarations.Any())
            {
                argDeclarations.RemoveAt(argDeclarations.Count - 1);
            }

            //Generate return type.
            var methodDeclaration = SyntaxFactory.MethodDeclaration(methodInfo.ReturnType == typeof(void) ? SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)) : returnDeclaration, SyntaxFactory.Identifier(methodInfo.Name));

            if (methodInfo.ReturnType.Namespace == typeof(Task).Namespace)
            {
                methodDeclaration = methodDeclaration.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                    SyntaxFactory.Token(SyntaxKind.AsyncKeyword)));
            }
            else
            {
                methodDeclaration = methodDeclaration.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)));
            }

            methodDeclaration = methodDeclaration.WithParameterList(
                SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList<ParameterSyntax>(argDeclarations)));


            ExpressionSyntax expressionSyntax;

            if (methodInfo.ReturnType == typeof(Task))
            {
                expressionSyntax = SyntaxFactory.IdentifierName("InvokeAsync");
            }
            else
            {
                expressionSyntax = SyntaxFactory.GenericName(SyntaxFactory.Identifier("InvokeAsync"))
                    .WithTypeArgumentList(((GenericNameSyntax)returnDeclaration).TypeArgumentList);
            }

            var argNames = methodInfo.GetParameters().Select(p => SyntaxFactory.IdentifierName(SyntaxFactory.Identifier(p.Name))).ToList();
            var token = new SyntaxNodeOrToken[]
            {
                SyntaxFactory.Argument(SyntaxFactory
                    .ArrayCreationExpression(SyntaxFactory
                        .ArrayType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)))
                        .WithRankSpecifiers(SyntaxFactory.SingletonList(
                            SyntaxFactory.ArrayRankSpecifier(
                                SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                                    SyntaxFactory.OmittedArraySizeExpression())))))
                    .WithInitializer(SyntaxFactory.InitializerExpression(SyntaxKind.CollectionInitializerExpression,
                        SyntaxFactory.SeparatedList<ExpressionSyntax>(argNames)))),

                SyntaxFactory.Token(SyntaxKind.CommaToken),
                SyntaxFactory.Argument(
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal(serviceRoute))),
                SyntaxFactory.Token(SyntaxKind.CommaToken),
                SyntaxFactory.Argument(
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal(serviceName)))
            };

            expressionSyntax = SyntaxFactory.AwaitExpression(SyntaxFactory.InvocationExpression(expressionSyntax)
                .WithArgumentList(
                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList<ArgumentSyntax>(token))
                ));

            StatementSyntax statementSyntax;
            if (methodInfo.ReturnType != typeof(Task) && methodInfo.ReturnType != typeof(void))
            {
                statementSyntax = SyntaxFactory.ReturnStatement(expressionSyntax);
            }
            else
            {
                statementSyntax = SyntaxFactory.ExpressionStatement(expressionSyntax);
            }

            return methodDeclaration.WithBody(SyntaxFactory.Block(SyntaxFactory.SingletonList(statementSyntax)));
        }

        private static ConstructorDeclarationSyntax GenerateConstructorDeclaration(string className)
        {
            return SyntaxFactory.ConstructorDeclaration(SyntaxFactory.Identifier(className))
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddParameterListParameters(SyntaxFactory.Parameter(SyntaxFactory.Identifier("remotingInvoke"))
                    .WithType(SyntaxFactory.IdentifierName("IRemotingInvoke")))
            .WithInitializer(SyntaxFactory.ConstructorInitializer(SyntaxKind.BaseConstructorInitializer,
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList<ArgumentSyntax>(
                    new SyntaxNodeOrToken[]
                    {
                        SyntaxFactory.Argument(SyntaxFactory.IdentifierName("remotingInvoke"))
                    }))))
            .WithBody(SyntaxFactory.Block());
        }
        private static TypeSyntax GenerateType(Type type)
        {
            if (!type.IsGenericType)
                return GenerateQualifiedNameSyntax(type);
            var list = new List<SyntaxNodeOrToken>();
            foreach (var genericType in type.GetGenericArguments())
            {
                list.Add(genericType.IsGenericType ? GenerateType(genericType) : GenerateQualifiedNameSyntax(genericType.FullName));
                list.Add(SyntaxFactory.Token(SyntaxKind.CommaToken));
            }

            var typeArgumentList = SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList<TypeSyntax>(list.Take(list.Count - 1)));
            return SyntaxFactory.GenericName(type.Name.Substring(0, type.Name.IndexOf('`'))).WithTypeArgumentList(typeArgumentList);
        }



        #region QualifiedNameSyntax

        private static QualifiedNameSyntax GenerateQualifiedNameSyntax(Type type)
        {
            return GenerateQualifiedNameSyntax($"{type.Namespace}.{type.Name}");
        }

        private static QualifiedNameSyntax GenerateQualifiedNameSyntax(string fullName)
        {
            return GenerateQualifiedNameSyntax(fullName.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static QualifiedNameSyntax GenerateQualifiedNameSyntax(IEnumerable<string> names)
        {
            var identifierNames = names.Select(SyntaxFactory.IdentifierName).ToArray();

            QualifiedNameSyntax left = null;
            for (var i = 0; i < identifierNames.Length - 1; i++)
            {
                left = left == null
                    ? SyntaxFactory.QualifiedName(identifierNames[i], identifierNames[i + 1])
                    : SyntaxFactory.QualifiedName(left, identifierNames[i + 1]);
            }
            return left;
        }
        #endregion


        private static MemoryStream CompileClientProxy(IEnumerable<SyntaxTree> trees, IEnumerable<MetadataReference> references)
        {
            var assemblies = new[]
            {
                "System.Runtime",
                "mscorlib",
                "System.Threading.Tasks",
                "System.Collections"
            };
            references = assemblies.Select(i => MetadataReference.CreateFromFile(Assembly.Load(new AssemblyName(i)).Location)).Concat(references);

            references = references.Concat(new[]
            {
                MetadataReference.CreateFromFile(typeof(Task).GetTypeInfo().Assembly.Location),
                MetadataReference.CreateFromFile(typeof(DynamicProxyAbstract).GetTypeInfo().Assembly.Location)
            });
            return Compile("Uragano.DynamicProxy.UraganoProxy", trees, references);
        }


        private static MemoryStream Compile(string assemblyName, IEnumerable<SyntaxTree> trees, IEnumerable<MetadataReference> references)
        {
            var compilation = CSharpCompilation.Create(assemblyName, trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var stream = new MemoryStream();
            var result = compilation.Emit(stream);
            if (!result.Success)
            {
                throw new Exception("Generate dynamic proxy failed:\n" + string.Join(";\n", result.Diagnostics.Select(i => i.ToString())));
            }
            stream.Seek(0, SeekOrigin.Begin);
            return stream;
        }
    }
}
